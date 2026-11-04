using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

namespace RouteGo.Fleet.Api.Infrastructure;

/// <summary>
/// Publishes Fleet events to the fleet events stream (an SNS topic), with the event type as the
/// message attribute subscription filter policies match on. Immutable and thread-safe.
/// </summary>
public sealed class SnsFleetEventPublisher
{
    private const string EventTypeAttribute = "eventType";
    private const string StringDataType = "String";

    private readonly IAmazonSimpleNotificationService _sns;
    private readonly string _topicArn;

    public SnsFleetEventPublisher(IAmazonSimpleNotificationService sns, string topicArn)
    {
        _sns = sns;
        _topicArn = topicArn;
    }

    /// <summary>Publishes one event envelope; throws when the messaging service does not accept it.</summary>
    public async Task PublishAsync(string eventType, string envelopeJson, CancellationToken cancellationToken)
    {
        var request = new PublishRequest
        {
            TopicArn = _topicArn,
            Message = envelopeJson,
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                [EventTypeAttribute] = new MessageAttributeValue { DataType = StringDataType, StringValue = eventType },
            },
        };
        await _sns.PublishAsync(request, cancellationToken);
    }
}
