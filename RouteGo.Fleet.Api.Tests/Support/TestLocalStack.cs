using Testcontainers.LocalStack;

namespace RouteGo.Fleet.Api.Tests.Support;

/// <summary>
/// One real AWS API (LocalStack) for the whole test run — S3 for the media store, SNS and SQS for
/// events: started on first use and removed with the test process.
/// </summary>
public static class TestLocalStack
{
    // Pinned: later LocalStack images require an account token.
    private const string LocalStackImage = "localstack/localstack:3.8.1";

    private static readonly SemaphoreSlim _startGate = new SemaphoreSlim(1, 1);
    private static LocalStackContainer? _container;

    /// <summary>The endpoint every client in a test points at.</summary>
    public static async Task<string> GetServiceUrlAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            if (_container is null)
            {
                LocalStackContainer container = new LocalStackBuilder(LocalStackImage).Build();
                await container.StartAsync();
                _container = container;
            }

            return _container.GetConnectionString();
        }
        finally
        {
            _startGate.Release();
        }
    }
}
