using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RouteGo.Fleet.Api.Infrastructure.Pgsql;

namespace RouteGo.Fleet.Api.Infrastructure.Messaging;

[Collection(DatabaseCollection.Name)]
public sealed class FleetQueueListenerTests
{
    // Nothing listens on port 1, so every attempt to reach this database fails the way an outage does.
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=fleet;Username=fleet;Password=fleet;Timeout=1";

    private const string CountProfilesSql = "SELECT count(*) FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId";
    private const string CompanyNameSql = "SELECT company_name FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId";

    // Two receives before parking, visible again a second after each, so a parking test takes seconds.
    private const int MaxReceiveCount = 2;
    private const int VisibilityTimeoutSeconds = 1;

    private readonly DatabaseFixture _database;
    private readonly CapturingLogger _logger = new CapturingLogger();

    public FleetQueueListenerTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ReceiveOnceAsync_ApprovalWaitingFromWhileFleetWasStopped_CreatesTheProfileAndRemovesTheMessage()
    {
        // Arrange: the approval reached the queue before this listener existed.
        using IAmazonSQS sqs = await TestMessaging.CreateSqsAsync();
        TestQueue queue = await TestMessaging.CreateQueueAsync(sqs, MaxReceiveCount, VisibilityTimeoutSeconds);
        string operatorId = Guid.NewGuid().ToString();
        await TestMessaging.SendAsync(sqs, queue.QueueUrl, Envelope("E1", operatorId, "BusCo Travels"));
        FleetQueueListener listener = Listener(sqs, queue, _database.ConnectionString);

        // Act
        await listener.ReceiveOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal("BusCo Travels", await _database.QueryScalarAsync<string>(CompanyNameSql, new { OperatorId = operatorId }));
        Assert.Empty(await TestMessaging.ListRemainingAsync(sqs, queue));
    }

    [Fact]
    public async Task ReceiveOnceAsync_UnusableEventData_RemovesTheMessageSoItIsNotRetried()
    {
        // Arrange
        using IAmazonSQS sqs = await TestMessaging.CreateSqsAsync();
        TestQueue queue = await TestMessaging.CreateQueueAsync(sqs, MaxReceiveCount, VisibilityTimeoutSeconds);
        string operatorId = Guid.NewGuid().ToString();
        await TestMessaging.SendAsync(sqs, queue.QueueUrl, Envelope("E9", operatorId, null));
        FleetQueueListener listener = Listener(sqs, queue, _database.ConnectionString);

        // Act
        await listener.ReceiveOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0L, await _database.QueryScalarAsync<long>(CountProfilesSql, new { OperatorId = operatorId }));
        Assert.Empty(await TestMessaging.ListRemainingAsync(sqs, queue));
        CapturedLog alert = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("OPERATOR_PROFILE_NOT_CREATED", alert.Message);
        Assert.Contains("invalidEventData", alert.Message);
    }

    [Fact]
    public async Task ReceiveOnceAsync_MessageThatIsNotAnEnvelope_RemovesItAndRaisesTheAlert()
    {
        // Arrange
        using IAmazonSQS sqs = await TestMessaging.CreateSqsAsync();
        TestQueue queue = await TestMessaging.CreateQueueAsync(sqs, MaxReceiveCount, VisibilityTimeoutSeconds);
        await TestMessaging.SendAsync(sqs, queue.QueueUrl, "not json");
        FleetQueueListener listener = Listener(sqs, queue, _database.ConnectionString);

        // Act
        await listener.ReceiveOnceAsync(CancellationToken.None);

        // Assert
        Assert.Empty(await TestMessaging.ListRemainingAsync(sqs, queue));
        CapturedLog alert = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("OPERATOR_PROFILE_NOT_CREATED", alert.Message);
    }

    [Fact]
    public async Task ReceiveOnceAsync_DatabaseDownThroughEveryAttempt_ParksTheEventAndAlertsOnce()
    {
        // Arrange
        using IAmazonSQS sqs = await TestMessaging.CreateSqsAsync();
        TestQueue queue = await TestMessaging.CreateQueueAsync(sqs, MaxReceiveCount, VisibilityTimeoutSeconds);
        string operatorId = Guid.NewGuid().ToString();
        await TestMessaging.SendAsync(sqs, queue.QueueUrl, Envelope("E7", operatorId, "BusCo Travels"));
        FleetQueueListener listener = Listener(sqs, queue, UnreachableDatabase);

        // Act
        await listener.ReceiveOnceAsync(CancellationToken.None);
        await listener.ReceiveOnceAsync(CancellationToken.None);
        IReadOnlyList<Message> parked = await TestMessaging.WaitForParkedAsync(sqs, queue);

        // Assert
        Message parkedEvent = Assert.Single(parked);
        Assert.Contains("E7", parkedEvent.Body);
        CapturedLog alert = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.StartsWith("OPERATOR_PROFILE_NOT_CREATED", alert.Message);
        Assert.Contains("E7", alert.Message);
        Assert.Contains("repeatedFailure", alert.Message);
        Assert.Equal(0L, await _database.QueryScalarAsync<long>(CountProfilesSql, new { OperatorId = operatorId }));
    }

    [Fact]
    public async Task ReceiveOnceAsync_ParkedEventReplayedAfterTheFix_CreatesTheProfile()
    {
        // Arrange: the event was parked during the outage and is now put back on the queue.
        using IAmazonSQS sqs = await TestMessaging.CreateSqsAsync();
        TestQueue queue = await TestMessaging.CreateQueueAsync(sqs, MaxReceiveCount, VisibilityTimeoutSeconds);
        string operatorId = Guid.NewGuid().ToString();
        await TestMessaging.SendAsync(sqs, queue.QueueUrl, Envelope("E7", operatorId, "BusCo Travels"));
        FleetQueueListener failing = Listener(sqs, queue, UnreachableDatabase);
        await failing.ReceiveOnceAsync(CancellationToken.None);
        await failing.ReceiveOnceAsync(CancellationToken.None);
        Message parked = Assert.Single(await TestMessaging.WaitForParkedAsync(sqs, queue));
        await TestMessaging.SendAsync(sqs, queue.QueueUrl, parked.Body);
        FleetQueueListener fixedListener = Listener(sqs, queue, _database.ConnectionString);

        // Act
        await fixedListener.ReceiveOnceAsync(CancellationToken.None);

        // Assert
        Assert.Equal("BusCo Travels", await _database.QueryScalarAsync<string>(CompanyNameSql, new { OperatorId = operatorId }));
    }

    // The envelope as Identity publishes it; a null company name leaves the field out.
    private static string Envelope(string eventId, string operatorId, string? companyName)
    {
        var data = new Dictionary<string, string> { ["operatorId"] = operatorId, ["email"] = "owner@busco.in" };
        if (companyName != null)
        {
            data["companyName"] = companyName;
        }

        var envelope = new
        {
            eventId,
            eventType = "OperatorApproved",
            version = 1,
            occurredAt = "2026-10-09T09:00:00Z",
            correlationId = "approve-test-correlation",
            producer = "identity-service",
            data,
        };
        return JsonSerializer.Serialize(envelope);
    }

    private FleetQueueListener Listener(IAmazonSQS sqs, TestQueue queue, string connectionString)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ConfigKeys.FleetQueueUrl] = queue.QueueUrl })
            .Build();
        var writerFactory = new WriterConnectionFactory(connectionString, SystemActor.Default, ConnectionRetryOptions.ManagedInstances);
        return new FleetQueueListener(sqs, configuration, writerFactory, _logger);
    }
}
