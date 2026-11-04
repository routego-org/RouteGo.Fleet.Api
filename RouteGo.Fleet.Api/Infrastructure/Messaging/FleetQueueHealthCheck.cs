using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RouteGo.Fleet.Api.Infrastructure.Messaging;

/// <summary>
/// Readiness: Fleet is ready only when it can receive its events, which means the fleet queue can be
/// reached. A not-ready instance must receive no traffic, so a failure answers unhealthy rather than
/// throwing.
/// </summary>
public sealed class FleetQueueHealthCheck : IHealthCheck
{
    private const string QueueArnAttribute = "QueueArn";

    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;

    public FleetQueueHealthCheck(IAmazonSQS sqs, IConfiguration configuration)
    {
        _sqs = sqs;
        _queueUrl = configuration[ConfigKeys.FleetQueueUrl]!;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var request = new GetQueueAttributesRequest
        {
            QueueUrl = _queueUrl,
            AttributeNames = new List<string> { QueueArnAttribute },
        };
        try
        {
            await _sqs.GetQueueAttributesAsync(request, cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (AmazonServiceException exception)
        {
            return HealthCheckResult.Unhealthy("The fleet queue cannot be read.", exception);
        }
        catch (AmazonClientException exception)
        {
            return HealthCheckResult.Unhealthy("The fleet queue cannot be reached.", exception);
        }
        catch (HttpRequestException exception)
        {
            return HealthCheckResult.Unhealthy("The fleet queue cannot be reached.", exception);
        }
    }
}
