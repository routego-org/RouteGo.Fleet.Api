using System.Globalization;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace RouteGo.Fleet.Api.Tests.Support;

/// <summary>A queue and the dead-letter queue it parks messages on.</summary>
/// <param name="QueueUrl">The queue a listener reads.</param>
/// <param name="DeadLetterQueueUrl">Where a message goes once its receive limit is reached.</param>
/// <param name="VisibilityTimeout">How long a received message stays hidden before it is delivered again.</param>
public sealed record TestQueue(string QueueUrl, string DeadLetterQueueUrl, TimeSpan VisibilityTimeout);

/// <summary>A topic and a queue subscribed to it with raw delivery, so a test can read what was published.</summary>
/// <param name="TopicArn">The topic a publisher sends to.</param>
/// <param name="SubscriberQueueUrl">The subscribed queue.</param>
public sealed record TestTopic(string TopicArn, string SubscriberQueueUrl);

/// <summary>
/// Real SNS and SQS (the run's LocalStack) for the event tests. Every queue and topic gets a fresh
/// name, so tests never read each other's messages.
/// </summary>
public static class TestMessaging
{
    // SNS hands a published message to its subscribed queue asynchronously, so a read waits briefly
    // for stragglers instead of reporting an empty queue the moment it is asked.
    private const int ReceiveWaitSeconds = 1;
    private const int MaxMessagesPerReceive = 10;
    private const int MaxParkingPolls = 20;

    private static readonly TimeSpan _parkingPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>An SQS client for the run's LocalStack.</summary>
    public static async Task<IAmazonSQS> CreateSqsAsync()
    {
        string serviceUrl = await TestLocalStack.GetServiceUrlAsync();
        return new AmazonSQSClient(new BasicAWSCredentials("local", "local"), new AmazonSQSConfig { ServiceURL = serviceUrl });
    }

    /// <summary>An SNS client for the run's LocalStack that remembers the order of its publishes.</summary>
    public static async Task<RecordingSnsClient> CreateSnsAsync()
    {
        string serviceUrl = await TestLocalStack.GetServiceUrlAsync();
        var config = new AmazonSimpleNotificationServiceConfig { ServiceURL = serviceUrl };
        return new RecordingSnsClient(new BasicAWSCredentials("local", "local"), config);
    }

    /// <summary>
    /// A new queue with a dead-letter queue after <paramref name="maxReceiveCount"/> receives, whose
    /// messages become visible again <paramref name="visibilityTimeoutSeconds"/> after a receive.
    /// </summary>
    public static async Task<TestQueue> CreateQueueAsync(IAmazonSQS sqs, int maxReceiveCount, int visibilityTimeoutSeconds)
    {
        string name = "fleet-q-" + Guid.NewGuid().ToString("N");
        CreateQueueResponse deadLetter = await sqs.CreateQueueAsync(name + "-dlq");
        string deadLetterArn = await GetQueueArnAsync(sqs, deadLetter.QueueUrl);
        string redrivePolicy = JsonSerializer.Serialize(new { deadLetterTargetArn = deadLetterArn, maxReceiveCount });
        var request = new CreateQueueRequest
        {
            QueueName = name,
            Attributes = new Dictionary<string, string>
            {
                ["RedrivePolicy"] = redrivePolicy,
                ["VisibilityTimeout"] = visibilityTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
            },
        };
        CreateQueueResponse queue = await sqs.CreateQueueAsync(request);
        return new TestQueue(queue.QueueUrl, deadLetter.QueueUrl, TimeSpan.FromSeconds(visibilityTimeoutSeconds));
    }

    /// <summary>A new topic with a raw-delivery queue subscribed to it.</summary>
    public static async Task<TestTopic> CreateTopicAsync(IAmazonSimpleNotificationService sns, IAmazonSQS sqs)
    {
        string name = "routego-fleet-events-" + Guid.NewGuid().ToString("N");
        CreateTopicResponse topic = await sns.CreateTopicAsync(name);
        CreateQueueResponse subscriber = await sqs.CreateQueueAsync(name + "-subscriber");
        string subscriberArn = await GetQueueArnAsync(sqs, subscriber.QueueUrl);
        var subscribe = new SubscribeRequest
        {
            TopicArn = topic.TopicArn,
            Protocol = "sqs",
            Endpoint = subscriberArn,
            Attributes = new Dictionary<string, string> { ["RawMessageDelivery"] = "true" },
        };
        await sns.SubscribeAsync(subscribe);
        return new TestTopic(topic.TopicArn, subscriber.QueueUrl);
    }

    /// <summary>Puts one message body on the queue, as Identity's topic would.</summary>
    public static async Task SendAsync(IAmazonSQS sqs, string queueUrl, string body)
    {
        await sqs.SendMessageAsync(queueUrl, body);
    }

    /// <summary>Every message on the queue, removed as they are read, in the order SQS returns them.</summary>
    public static async Task<IReadOnlyList<Message>> ReceiveAllAsync(IAmazonSQS sqs, string queueUrl)
    {
        var received = new List<Message>();
        List<Message> batch = await ReceiveBatchAsync(sqs, queueUrl);
        while (batch.Count > 0)
        {
            foreach (Message message in batch)
            {
                received.Add(message);
                await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle);
            }

            batch = await ReceiveBatchAsync(sqs, queueUrl);
        }

        return received;
    }

    /// <summary>
    /// Every message still on the queue or its dead-letter queue once a received-but-kept message
    /// would have become visible again — empty when the listener deleted what it received.
    /// </summary>
    public static async Task<IReadOnlyList<Message>> ListRemainingAsync(IAmazonSQS sqs, TestQueue queue)
    {
        await Task.Delay(queue.VisibilityTimeout);
        var remaining = new List<Message>();
        remaining.AddRange(await ReceiveAllAsync(sqs, queue.QueueUrl));
        remaining.AddRange(await ReceiveAllAsync(sqs, queue.DeadLetterQueueUrl));
        return remaining;
    }

    /// <summary>
    /// Waits until the queue has parked its messages on the dead-letter queue and returns them. SQS
    /// moves a message once it is received past its limit, so the queue is polled until it does.
    /// </summary>
    public static async Task<IReadOnlyList<Message>> WaitForParkedAsync(IAmazonSQS sqs, TestQueue queue)
    {
        for (int attempt = 0; attempt < MaxParkingPolls; attempt++)
        {
            await Task.Delay(_parkingPollInterval);
            await ReceiveBatchAsync(sqs, queue.QueueUrl);
            IReadOnlyList<Message> parked = await ReceiveAllAsync(sqs, queue.DeadLetterQueueUrl);
            if (parked.Count > 0)
            {
                return parked;
            }
        }

        return Array.Empty<Message>();
    }

    private static async Task<List<Message>> ReceiveBatchAsync(IAmazonSQS sqs, string queueUrl)
    {
        var request = new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            MaxNumberOfMessages = MaxMessagesPerReceive,
            WaitTimeSeconds = ReceiveWaitSeconds,
            MessageAttributeNames = new List<string> { "All" },
        };
        ReceiveMessageResponse response = await sqs.ReceiveMessageAsync(request);
        if (response.Messages is null)
        {
            return new List<Message>();
        }

        return response.Messages;
    }

    private static async Task<string> GetQueueArnAsync(IAmazonSQS sqs, string queueUrl)
    {
        GetQueueAttributesResponse attributes = await sqs.GetQueueAttributesAsync(queueUrl, new List<string> { "QueueArn" });
        return attributes.QueueARN;
    }
}
