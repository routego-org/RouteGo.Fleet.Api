namespace RouteGo.Fleet.Api.Tests;

/// <summary>
/// Starts the real app WITH its background workers. Every other host in the suite leaves them out
/// (FleetTestHost), so this is the one place the container is asked to build them: a wiring mistake
/// there — a worker handed the request-scoped writer — stops the deployed app at start-up, and is
/// caught here instead of on the server.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HostStartupTests
{
    private readonly DatabaseFixture _database;

    public HostStartupTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task Start_WithBackgroundWorkers_ServesHealth()
    {
        // Arrange
        using ApiFactory factory = TestHost.For(_database);
        HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
