using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using RouteGo.Fleet.Api.Features.OperatorProfiles.CreateOperatorProfile;

namespace RouteGo.Fleet.Api.Infrastructure.Messaging;

/// <summary>
/// Reads the fleet queue for as long as the process runs and turns each OperatorApproved event into
/// an operator profile, one message at a time. A message whose event is handled — or can never be —
/// is deleted; one that must be tried again is left, so the queue delivers it again after its
/// visibility timeout and, once its receive limit is reached, parks it on the dead-letter queue.
/// Events that arrive while Fleet is stopped wait on the queue until it starts again.
/// </summary>
public sealed class FleetQueueListener : BackgroundService
{
    private const int MaxMessagesPerReceive = 10;
    private const int LongPollSeconds = 20;
    private const string ReceiveCountAttribute = "ApproximateReceiveCount";
    private const string RedrivePolicyAttribute = "RedrivePolicy";

    // How long to wait before trying the queue again after it could not be reached, so an outage is
    // retried steadily instead of in a tight loop.
    private static readonly TimeSpan _queueUnreachableDelay = TimeSpan.FromSeconds(10);

    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private readonly CreateOperatorProfileHandler _handler;
    private readonly ILogger<FleetQueueListener> _logger;
    private int? _maxReceiveCount;

    public FleetQueueListener(
        IAmazonSQS sqs,
        IConfiguration configuration,
        IWriterConnectionFactory writerFactory,
        ILoggerFactory loggerFactory)
    {
        _sqs = sqs;
        _queueUrl = configuration[ConfigKeys.FleetQueueUrl]!;
        _handler = new CreateOperatorProfileHandler(writerFactory, loggerFactory.CreateLogger<CreateOperatorProfileHandler>());
        _logger = loggerFactory.CreateLogger<FleetQueueListener>();
    }

    /// <summary>Receives one batch of messages (waiting up to 20 seconds for one) and handles each.</summary>
    public async Task ReceiveOnceAsync(CancellationToken cancellationToken)
    {
        if (_maxReceiveCount is null)
        {
            _maxReceiveCount = await ReadMaxReceiveCountAsync(cancellationToken);
        }

        var request = new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = MaxMessagesPerReceive,
            WaitTimeSeconds = LongPollSeconds,
            MessageSystemAttributeNames = new List<string> { ReceiveCountAttribute },
        };
        ReceiveMessageResponse response = await _sqs.ReceiveMessageAsync(request, cancellationToken);
        if (response.Messages is null)
        {
            return;
        }

        foreach (Message message in response.Messages)
        {
            await HandleMessageAsync(message, _maxReceiveCount.Value, cancellationToken);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReceiveOnceAsync(stoppingToken);
            }
            catch (AmazonServiceException exception)
            {
                _logger.LogError(exception, "Fleet queue {QueueUrl} could not be read; retrying", _queueUrl);
                await Task.Delay(_queueUnreachableDelay, stoppingToken);
            }
            catch (AmazonClientException exception)
            {
                _logger.LogError(exception, "Fleet queue {QueueUrl} could not be reached; retrying", _queueUrl);
                await Task.Delay(_queueUnreachableDelay, stoppingToken);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogError(exception, "Fleet queue {QueueUrl} could not be reached; retrying", _queueUrl);
                await Task.Delay(_queueUnreachableDelay, stoppingToken);
            }
        }
    }

    // The receive limit is read from the queue's own redrive policy, so the last attempt this
    // process acts on is always the one the queue enforces.
    private async Task<int> ReadMaxReceiveCountAsync(CancellationToken cancellationToken)
    {
        var request = new GetQueueAttributesRequest
        {
            QueueUrl = _queueUrl,
            AttributeNames = new List<string> { RedrivePolicyAttribute },
        };
        GetQueueAttributesResponse response = await _sqs.GetQueueAttributesAsync(request, cancellationToken);
        string? policy = null;
        if (response.Attributes != null)
        {
            response.Attributes.TryGetValue(RedrivePolicyAttribute, out policy);
        }

        int? maxReceiveCount = RedrivePolicy.ReadMaxReceiveCount(policy);
        if (maxReceiveCount is null)
        {
            throw new InvalidOperationException(
                $"Queue {_queueUrl} has no dead-letter redrive policy with a maxReceiveCount; "
                + "without it a failing event is retried forever and never parked.");
        }

        return maxReceiveCount.Value;
    }

    private async Task HandleMessageAsync(Message message, int maxReceiveCount, CancellationToken cancellationToken)
    {
        var envelope = EventEnvelope.Read(message.Body);
        if (envelope is null)
        {
            _logger.LogError(
                "{Code}: queue message {MessageId} created no profile, reason invalidEventData: it is not a RouteGo event "
                + "envelope with an event ID, event type, time and correlation ID; it will not be retried",
                ProblemTypes.OperatorProfileNotCreated.Code, message.MessageId);
            await _sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, cancellationToken);
            return;
        }

        if (envelope.EventType != CreateOperatorProfileHandler.TriggerEventType)
        {
            _logger.LogError(
                "Queue message {MessageId} carries event type {EventType}, which Fleet does not handle; left for the dead-letter queue",
                message.MessageId, envelope.EventType);
            return;
        }

        var scope = new Dictionary<string, object> { ["CorrelationId"] = envelope.CorrelationId, ["EventId"] = envelope.EventId };
        using (_logger.BeginScope(scope))
        {
            bool isLastAttempt = ReadReceiveCount(message) >= maxReceiveCount;
            await HandleApprovalAsync(message, envelope, isLastAttempt, cancellationToken);
        }
    }

    private async Task HandleApprovalAsync(
        Message message, EventEnvelope envelope, bool isLastAttempt, CancellationToken cancellationToken)
    {
        CreateOperatorProfileOutcome? outcome = await HandleSafelyAsync(envelope, isLastAttempt, cancellationToken);
        if (outcome is null || outcome == CreateOperatorProfileOutcome.RetryLater)
        {
            return;
        }

        await _sqs.DeleteMessageAsync(_queueUrl, message.ReceiptHandle, cancellationToken);
    }

    // A fault inside one event must not stop the listener: the message stays on the queue, is
    // delivered again, and is parked once its receive limit is reached.
    private async Task<CreateOperatorProfileOutcome?> HandleSafelyAsync(
        EventEnvelope envelope, bool isLastAttempt, CancellationToken cancellationToken)
    {
        var delivery = new OperatorApprovedDelivery
        {
            EventId = envelope.EventId,
            OccurredAt = envelope.OccurredAt,
            CorrelationId = envelope.CorrelationId,
            Data = envelope.Data,
            IsLastAttempt = isLastAttempt,
        };
        try
        {
            return await _handler.HandleAsync(delivery, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(
                exception, "OperatorApproved event {EventId} (correlation {CorrelationId}) failed; it will be delivered again",
                envelope.EventId, envelope.CorrelationId);
            return null;
        }
    }

    private static int ReadReceiveCount(Message message)
    {
        string? text = null;
        if (message.Attributes != null)
        {
            message.Attributes.TryGetValue(ReceiveCountAttribute, out text);
        }

        bool isNumber = int.TryParse(text, out int receiveCount);
        if (!isNumber)
        {
            return 1;
        }

        return receiveCount;
    }
}
