using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;

namespace RouteGo.Fleet.Api.Tests.Support;

/// <summary>
/// A real SNS client that also remembers every publish SNS accepted, in the order it was made. SNS
/// hands messages to subscribers asynchronously and may reorder them, so publish order can only be
/// observed here, at the sender.
/// </summary>
public sealed class RecordingSnsClient : AmazonSimpleNotificationServiceClient
{
    private readonly List<PublishRequest> _published = new List<PublishRequest>();

    public RecordingSnsClient(AWSCredentials credentials, AmazonSimpleNotificationServiceConfig config)
        : base(credentials, config)
    {
    }

    /// <summary>Every accepted publish, in the order it was made.</summary>
    public IReadOnlyList<PublishRequest> Published
    {
        get
        {
            return _published;
        }
    }

    public override async Task<PublishResponse> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        PublishResponse response = await base.PublishAsync(request, cancellationToken);
        _published.Add(request);
        return response;
    }
}
