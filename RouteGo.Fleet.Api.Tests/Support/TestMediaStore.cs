using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;

namespace RouteGo.Fleet.Api.Tests.Support;

/// <summary>
/// A real S3 API (the run's LocalStack) standing in for the media store, shared by every photo test
/// in the run. The host keeps its own S3 adapter; only the client underneath is pointed here, so
/// presigning, uploads and checks all run for real.
/// </summary>
public static class TestMediaStore
{
    // The bucket LocalEntryPoints names for Development, which is how every test host runs.
    private const string Bucket = "routego-fleet-media-local";

    private static readonly SemaphoreSlim _bucketGate = new SemaphoreSlim(1, 1);
    private static readonly MediaStoreBrowser _browser = CreateBrowser();
    private static bool _isBucketCreated;

    /// <summary>A host override that sends the media store's S3 calls to the test container.</summary>
    public static async Task<Action<IServiceCollection>> OverrideAsync()
    {
        IAmazonS3 client = await CreateClientAsync();
        return services => services.AddSingleton(client);
    }

    /// <summary>What the operator's browser does with an upload slot: PUT the file straight to the media store.</summary>
    public static async Task<HttpStatusCode> UploadAsync(string uploadUrl, string contentType, byte[] content)
    {
        return await _browser.UploadAsync(uploadUrl, contentType, content);
    }

    /// <summary>Stores a file at <paramref name="fileLocation"/>, as a completed browser upload would have.</summary>
    public static async Task PutFileAsync(string fileLocation, string contentType, byte[] content)
    {
        using IAmazonS3 client = await CreateClientAsync();
        using var stream = new MemoryStream(content);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = fileLocation,
            ContentType = contentType,
            InputStream = stream,
        });
    }

    /// <summary>Whether a file is stored at <paramref name="fileLocation"/>.</summary>
    public static async Task<bool> ExistsAsync(string fileLocation)
    {
        using IAmazonS3 client = await CreateClientAsync();
        ListObjectsV2Response listing = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = Bucket,
            Prefix = fileLocation,
        });
        return listing.KeyCount > 0;
    }

    private static async Task<IAmazonS3> CreateClientAsync()
    {
        string serviceUrl = await TestLocalStack.GetServiceUrlAsync();
        IAmazonS3 client = ConnectTo(serviceUrl);
        await EnsureBucketAsync(client);
        return client;
    }

    private static IAmazonS3 ConnectTo(string serviceUrl)
    {
        var config = new AmazonS3Config { ServiceURL = serviceUrl, ForcePathStyle = true };
        return new AmazonS3Client(new BasicAWSCredentials("local", "local"), config);
    }

    private static async Task EnsureBucketAsync(IAmazonS3 client)
    {
        await _bucketGate.WaitAsync();
        try
        {
            if (!_isBucketCreated)
            {
                await client.PutBucketAsync(Bucket);
                _isBucketCreated = true;
            }
        }
        finally
        {
            _bucketGate.Release();
        }
    }

    private static MediaStoreBrowser CreateBrowser()
    {
        var services = new ServiceCollection();
        services.AddHttpClient<MediaStoreBrowser>();
        ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<MediaStoreBrowser>();
    }
}
