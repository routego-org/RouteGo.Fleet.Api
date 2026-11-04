using System.Text.Json;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using RouteGo.Fleet.Api.Infrastructure;
using RouteGo.Fleet.Api.Infrastructure.Pgsql;

namespace RouteGo.Fleet.Api.Features.Trips.PublishFleetEvents;

[Collection(DatabaseCollection.Name)]
public sealed class PublishFleetEventsTests : IAsyncLifetime
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);

    // One operator's bus, route, schedule and trip, for the events to describe.
    private const string InsertTripSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Sleeper', true, NULL, 'Active');
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, 'Active');
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Pune, 210);
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '22:30', ARRAY['Friday'], 'Active', '2026-10-23');
        INSERT INTO trips.trips (trip_id, schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status)
        VALUES (@TripId, @ScheduleId, @OperatorId, @RouteId, @BusId, '2026-10-23', '2026-10-23T17:00:00Z', '2026-10-23T20:30:00Z', 'Scheduled');
        """;

    private const string InsertEventSql = """
        INSERT INTO trips.fleet_events (event_id, event_type, trip_id, data, correlation_id, recorded_at)
        VALUES (@EventId, @EventType, @TripId, CAST(@Data AS jsonb), @CorrelationId, @RecordedAt)
        """;

    // Events other tests left behind are not this test's to deliver or alert on.
    private const string SettleOtherEventsSql = """
        UPDATE trips.fleet_events SET delivered_at = recorded_at, delay_alerted_at = recorded_at WHERE delivered_at IS NULL
        """;

    private const string DeliveredAtSql = "SELECT delivered_at FROM trips.fleet_events WHERE event_id = @EventId";

    private readonly DatabaseFixture _database;
    private readonly CapturingLogger _logger = new CapturingLogger();
    private IAmazonSQS? _sqs;
    private RecordingSnsClient? _sns;
    private TestTopic? _topic;
    private Guid _tripId;

    public PublishFleetEventsTests(DatabaseFixture database)
    {
        _database = database;
    }

    public async Task InitializeAsync()
    {
        await _database.SeedAsync(SettleOtherEventsSql);
        _tripId = await SeedTripAsync();
        _sqs = await TestMessaging.CreateSqsAsync();
        _sns = await TestMessaging.CreateSnsAsync();
        _topic = await TestMessaging.CreateTopicAsync(_sns, _sqs);
    }

    public Task DisposeAsync()
    {
        _sqs?.Dispose();
        _sns?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleAsync_RecordedEvent_DeliversItWithTheEnvelopeAndMarksItDelivered()
    {
        // Arrange
        Guid eventId = await RecordEventAsync("TripScheduled", _now.AddSeconds(-3));

        // Act
        PublishFleetEventsResult result = await Handler(Publisher()).HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, result.DeliveredCount);
        Message message = Assert.Single(await PublishedAsync());
        Assert.Equal("TripScheduled", message.MessageAttributes["eventType"].StringValue);
        using var envelope = JsonDocument.Parse(message.Body);
        JsonElement root = envelope.RootElement;
        Assert.Equal(eventId, root.GetProperty("eventId").GetGuid());
        Assert.Equal("TripScheduled", root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(_now.AddSeconds(-3), root.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal("corr-" + eventId, root.GetProperty("correlationId").GetString());
        Assert.Equal("fleet-service", root.GetProperty("producer").GetString());
        Assert.Equal(_tripId, root.GetProperty("data").GetProperty("tripId").GetGuid());
        Assert.Equal(_now, await DeliveredAtAsync(eventId));
    }

    [Fact]
    public async Task HandleAsync_ThreeEventsCommittedInOrder_PublishesThemInThatOrder()
    {
        // Arrange: recorded times out of order, so only the commit order can produce the expected order.
        Guid first = await RecordEventAsync("TripScheduled", _now.AddSeconds(-1));
        Guid second = await RecordEventAsync("FareChanged", _now.AddSeconds(-3));
        Guid third = await RecordEventAsync("TripCancelled", _now.AddSeconds(-2));

        // Act
        await Handler(Publisher()).HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(new[] { first, second, third }, PublishedEventIds());
    }

    [Fact]
    public async Task HandleAsync_EarlierChangeStillBeingWritten_HoldsBackLaterEventsUntilItCommits()
    {
        // Arrange: E1's change is still in progress while E2's has already committed.
        await using var openChange = new NpgsqlConnection(_database.ConnectionString);
        await openChange.OpenAsync();
        await using NpgsqlTransaction transaction = await openChange.BeginTransactionAsync();
        var first = Guid.NewGuid();
        await openChange.ExecuteAsync(InsertEventSql, EventParameters(first, "TripScheduled", _now.AddSeconds(-2)), transaction);
        Guid second = await RecordEventAsync("TripCancelled", _now.AddSeconds(-1));
        PublishFleetEventsHandler handler = Handler(Publisher());
        PublishFleetEventsResult whileOpen = await handler.HandleAsync(CancellationToken.None);
        await transaction.CommitAsync();

        // Act
        PublishFleetEventsResult afterCommit = await handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, whileOpen.DeliveredCount);
        Assert.Equal(2, afterCommit.DeliveredCount);
        Assert.Equal(new[] { first, second }, PublishedEventIds());
    }

    [Fact]
    public async Task HandleAsync_EventAlreadyDelivered_IsNotDeliveredAgain()
    {
        // Arrange
        await RecordEventAsync("TripScheduled", _now.AddSeconds(-3));
        PublishFleetEventsHandler handler = Handler(Publisher());
        await handler.HandleAsync(CancellationToken.None);

        // Act
        PublishFleetEventsResult second = await handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, second.DeliveredCount);
        Assert.Single(await PublishedAsync());
    }

    [Fact]
    public async Task HandleAsync_MessagingUnavailable_KeepsEventsThenDeliversThemInOrderOnRecovery()
    {
        // Arrange
        Guid first = await RecordEventAsync("TripScheduled", _now.AddSeconds(-3));
        Guid second = await RecordEventAsync("TripCancelled", _now.AddSeconds(-2));
        PublishFleetEventsResult duringOutage = await Handler(UnavailablePublisher()).HandleAsync(CancellationToken.None);
        DateTimeOffset? deliveredDuringOutage = await DeliveredAtAsync(first);

        // Act
        PublishFleetEventsResult afterRecovery = await Handler(Publisher()).HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, duringOutage.DeliveredCount);
        Assert.Null(deliveredDuringOutage);
        Assert.Equal(2, afterRecovery.DeliveredCount);
        Assert.Equal(new[] { first, second }, PublishedEventIds());
    }

    [Fact]
    public async Task HandleAsync_DeliveryConfirmationLost_RedeliversWithTheSameEventId()
    {
        // Arrange: the first run delivered the event, then its confirmation was lost.
        Guid eventId = await RecordEventAsync("TripScheduled", _now.AddSeconds(-3));
        PublishFleetEventsHandler handler = Handler(Publisher());
        await handler.HandleAsync(CancellationToken.None);
        await _database.SeedAsync("UPDATE trips.fleet_events SET delivered_at = NULL WHERE event_id = @EventId", new { EventId = eventId });

        // Act
        await handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(new[] { eventId, eventId }, PublishedEventIds());
    }

    [Fact]
    public async Task HandleAsync_EventUndeliveredForOverFiveMinutes_RaisesTheAlertOnce()
    {
        // Arrange
        Guid eventId = await RecordEventAsync("FareChanged", _now.AddMinutes(-5).AddSeconds(-1));
        PublishFleetEventsHandler handler = Handler(UnavailablePublisher());

        // Act
        PublishFleetEventsResult firstRun = await handler.HandleAsync(CancellationToken.None);
        PublishFleetEventsResult secondRun = await handler.HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, firstRun.AlertedCount);
        Assert.Equal(0, secondRun.AlertedCount);
        CapturedLog alert = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.StartsWith("EVENT_PUBLISH_DELAYED", alert.Message);
        Assert.Contains(eventId.ToString(), alert.Message);
        Assert.Contains("FareChanged", alert.Message);
    }

    [Fact]
    public async Task HandleAsync_EventUndeliveredForUnderFiveMinutes_RaisesNoAlert()
    {
        // Arrange
        await RecordEventAsync("FareChanged", _now.AddMinutes(-4));

        // Act
        PublishFleetEventsResult result = await Handler(UnavailablePublisher()).HandleAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, result.AlertedCount);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    private PublishFleetEventsHandler Handler(SnsFleetEventPublisher publisher)
    {
        var writerFactory = new WriterConnectionFactory(
            _database.ConnectionString, SystemActor.Default, ConnectionRetryOptions.ManagedInstances);
        return new PublishFleetEventsHandler(writerFactory, publisher, new FixedClock(_now), _logger);
    }

    private SnsFleetEventPublisher Publisher()
    {
        return new SnsFleetEventPublisher(_sns!, _topic!.TopicArn);
    }

    // A topic that does not exist: SNS refuses every publish, as it would during an outage.
    private SnsFleetEventPublisher UnavailablePublisher()
    {
        return new SnsFleetEventPublisher(_sns!, _topic!.TopicArn + "-missing");
    }

    private async Task<IReadOnlyList<Message>> PublishedAsync()
    {
        return await TestMessaging.ReceiveAllAsync(_sqs!, _topic!.SubscriberQueueUrl);
    }

    // The order the publisher sent events in; the subscriber queue may reorder them.
    private Guid[] PublishedEventIds()
    {
        return _sns!.Published.Select(ReadEventId).ToArray();
    }

    private static Guid ReadEventId(PublishRequest request)
    {
        using var envelope = JsonDocument.Parse(request.Message);
        return envelope.RootElement.GetProperty("eventId").GetGuid();
    }

    private async Task<Guid> RecordEventAsync(string eventType, DateTimeOffset recordedAt)
    {
        var eventId = Guid.NewGuid();
        await _database.SeedAsync(InsertEventSql, EventParameters(eventId, eventType, recordedAt));
        return eventId;
    }

    private object EventParameters(Guid eventId, string eventType, DateTimeOffset recordedAt)
    {
        return new
        {
            EventId = eventId,
            EventType = eventType,
            TripId = _tripId,
            Data = JsonSerializer.Serialize(new { tripId = _tripId }),
            CorrelationId = "corr-" + eventId,
            RecordedAt = recordedAt,
        };
    }

    private async Task<Guid> SeedTripAsync()
    {
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        var tripId = Guid.NewGuid();
        await _database.SeedAsync(InsertTripSql, new
        {
            BusId = Guid.NewGuid(),
            RouteId = Guid.NewGuid(),
            ScheduleId = Guid.NewGuid(),
            TripId = tripId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "PE" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            Mumbai = _mumbai,
            Pune = _pune,
        });
        return tripId;
    }

    private async Task<DateTimeOffset?> DeliveredAtAsync(Guid eventId)
    {
        DateTime? deliveredAt = await _database.QueryScalarAsync<DateTime?>(DeliveredAtSql, new { EventId = eventId });
        if (deliveredAt is null)
        {
            return null;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(deliveredAt.Value, DateTimeKind.Utc));
    }
}
