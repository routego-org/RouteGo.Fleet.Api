using Amazon.Runtime;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using Amazon.SQS;

namespace RouteGo.Fleet.Api.Infrastructure;

/// <summary>
/// The AWS clients this service uses: real AWS through the execution role when deployed, or the one
/// AWS-compatible endpoint a developer machine names (LocalStack) for every service at once. Each
/// client is thread-safe, so one instance serves the whole process.
/// </summary>
public static class AwsClients
{
    // LocalStack checks no signatures, so a local run needs no AWS account; the placeholder keys only
    // let the SDK sign the way it must, and are worthless anywhere else.
    private const string LocalAccessKey = "local";
    private const string LocalSecretKey = "local";

    /// <summary>The S3 client, addressed path-style locally because a local endpoint has no per-bucket host names.</summary>
    public static IAmazonS3 CreateS3(IConfiguration configuration)
    {
        string? serviceUrl = configuration[ConfigKeys.AwsServiceUrl];
        if (string.IsNullOrEmpty(serviceUrl))
        {
            return new AmazonS3Client();
        }

        var config = new AmazonS3Config { ServiceURL = serviceUrl, ForcePathStyle = true };
        return new AmazonS3Client(LocalCredentials(), config);
    }

    /// <summary>The SNS client Fleet publishes its trip events with.</summary>
    public static IAmazonSimpleNotificationService CreateSns(IConfiguration configuration)
    {
        string? serviceUrl = configuration[ConfigKeys.AwsServiceUrl];
        if (string.IsNullOrEmpty(serviceUrl))
        {
            return new AmazonSimpleNotificationServiceClient();
        }

        var config = new AmazonSimpleNotificationServiceConfig { ServiceURL = serviceUrl };
        return new AmazonSimpleNotificationServiceClient(LocalCredentials(), config);
    }

    /// <summary>The SQS client Fleet reads the events it consumes with.</summary>
    public static IAmazonSQS CreateSqs(IConfiguration configuration)
    {
        string? serviceUrl = configuration[ConfigKeys.AwsServiceUrl];
        if (string.IsNullOrEmpty(serviceUrl))
        {
            return new AmazonSQSClient();
        }

        var config = new AmazonSQSConfig { ServiceURL = serviceUrl };
        return new AmazonSQSClient(LocalCredentials(), config);
    }

    private static BasicAWSCredentials LocalCredentials()
    {
        return new BasicAWSCredentials(LocalAccessKey, LocalSecretKey);
    }
}
