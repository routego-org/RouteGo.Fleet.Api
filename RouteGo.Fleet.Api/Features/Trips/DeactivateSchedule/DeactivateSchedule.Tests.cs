using System.Text.Json;
using RouteGo.Fleet.Api.Common.Behaviors;

namespace RouteGo.Fleet.Api.Features.Trips.DeactivateSchedule;

[Collection(DatabaseCollection.Name)]
public sealed class DeactivateScheduleTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 10, 8, 30, 0, TimeSpan.Zero);

    // One departed trip, five future trips, and one future trip the operator already cancelled.
    private static readonly DateTimeOffset[] _departures = new[]
    {
        new DateTimeOffset(2026, 10, 9, 16, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 10, 16, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 11, 16, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 12, 16, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 13, 16, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 14, 16, 30, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 10, 15, 16, 30, 0, TimeSpan.Zero),
    };

    private static readonly string[] _statuses = new[]
    {
        "Scheduled", "Scheduled", "Scheduled", "Scheduled", "Scheduled", "Scheduled", "Cancelled",
    };

    // Fares are stored in paise. A cancelled trip carries the cause, reason and time the trips
    // table requires of every cancelled trip.
    private const string InsertScheduleSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Seater', true, NULL, 'Active');
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 5, 2, 1);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Seater', 1);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'S1', 'Lower', 1, 1, 'Seater');
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, 'Active');
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Pune, 210);
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '22:00', ARRAY['Monday'], @Status, '2026-10-22');
        INSERT INTO trips.schedule_class_fares (schedule_id, seat_class, fare)
        VALUES (@ScheduleId, 'Seater', 45000);
        INSERT INTO trips.trips (schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status,
                                 cancellation_cause, cancel_reason, cancelled_at)
        SELECT @ScheduleId, @OperatorId, @RouteId, @BusId, (t.departure_at AT TIME ZONE 'Asia/Kolkata')::date,
               t.departure_at, t.departure_at + interval '210 minutes', t.status,
               CASE WHEN t.status = 'Cancelled' THEN 'Operator' END,
               CASE WHEN t.status = 'Cancelled' THEN 'Bus sent for repair' END,
               CASE WHEN t.status = 'Cancelled' THEN t.departure_at - interval '1 day' END
        FROM unnest(@Departures, @Statuses) AS t(departure_at, status);
        """;

    // Each trip as "status/cause", in departure order.
    private const string TripStatesSql = """
        SELECT string_agg(status || '/' || coalesce(cancellation_cause, '-'), ',' ORDER BY departure_at)
        FROM trips.trips WHERE schedule_id = @ScheduleId
        """;

    private const string DeactivatedTripsSql = """
        SELECT count(*) || ' ' || string_agg(DISTINCT cancel_reason || '@' || to_char(cancelled_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI'), ',')
        FROM trips.trips WHERE schedule_id = @ScheduleId AND cancellation_cause = 'ScheduleDeactivated'
        """;

    private const string EventsSql = """
        SELECT count(*) || ' ' || coalesce(string_agg(DISTINCT e.event_type || '/' || (e.data ->> 'reason') || '/' || e.correlation_id, ','), '-')
            || ' ' || coalesce(bool_and((e.data ->> 'tripId')::uuid = e.trip_id)::text, '-')
        FROM trips.fleet_events e JOIN trips.trips t ON t.trip_id = e.trip_id
        WHERE t.schedule_id = @ScheduleId
        """;

    private const string StatusSql = """
        SELECT status FROM trips.schedules WHERE schedule_id = @ScheduleId
        """;

    private readonly DatabaseFixture _database;

    public DeactivateScheduleTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task DeactivateSchedule_ActiveScheduleWithFutureTrips_BecomesInactiveAndReportsFiveCancelled()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid scheduleId = await SeedScheduleAsync(operatorId, "Active");

        // Act
        using HttpResponseMessage response = await DeactivateScheduleAsync(TestAccessTokens.CreateOperator(operatorId), scheduleId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var change = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = change.RootElement;
        Assert.Equal(scheduleId, root.GetProperty("schedule").GetProperty("scheduleId").GetGuid());
        Assert.Equal("inactive", root.GetProperty("schedule").GetProperty("status").GetString());
        Assert.Equal("[{\"seatClass\":\"Seater\",\"fare\":450}]", root.GetProperty("schedule").GetProperty("classFares").GetRawText());
        Assert.Equal(0, root.GetProperty("createdTripCount").GetInt32());
        Assert.Equal(5, root.GetProperty("cancelledTripCount").GetInt32());
        Assert.Equal(0, root.GetProperty("repricedTripCount").GetInt32());
        Assert.Equal("Inactive", await StatusAsync(scheduleId));
    }

    [Fact]
    public async Task DeactivateSchedule_ActiveScheduleWithFutureTrips_CancelsOnlyTheFutureTripsAndRecordsTheirEvents()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid scheduleId = await SeedScheduleAsync(operatorId, "Active");

        // Act
        using HttpResponseMessage response = await DeactivateScheduleAsync(TestAccessTokens.CreateOperator(operatorId), scheduleId);

        // Assert
        var lookup = new { ScheduleId = scheduleId };
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "Scheduled/-,Cancelled/ScheduleDeactivated,Cancelled/ScheduleDeactivated,Cancelled/ScheduleDeactivated,"
            + "Cancelled/ScheduleDeactivated,Cancelled/ScheduleDeactivated,Cancelled/Operator",
            await _database.QueryScalarAsync<string>(TripStatesSql, lookup));
        Assert.Equal("5 Schedule deactivated@2026-10-10T08:30", await _database.QueryScalarAsync<string>(DeactivatedTripsSql, lookup));
        Assert.Equal("5 TripCancelled/Schedule deactivated/deactivate-test-correlation true",
            await _database.QueryScalarAsync<string>(EventsSql, lookup));
    }

    [Fact]
    public async Task DeactivateSchedule_AlreadyInactive_ChangesNothingAndReportsNoneCancelled()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid scheduleId = await SeedScheduleAsync(operatorId, "Inactive");

        // Act
        using HttpResponseMessage response = await DeactivateScheduleAsync(TestAccessTokens.CreateOperator(operatorId), scheduleId);

        // Assert
        var lookup = new { ScheduleId = scheduleId };
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var change = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("inactive", change.RootElement.GetProperty("schedule").GetProperty("status").GetString());
        Assert.Equal(0, change.RootElement.GetProperty("cancelledTripCount").GetInt32());
        Assert.Equal("0 - -", await _database.QueryScalarAsync<string>(EventsSql, lookup));
        Assert.StartsWith("Scheduled/-,Scheduled/-", await _database.QueryScalarAsync<string>(TripStatesSql, lookup));
    }

    [Fact]
    public async Task DeactivateSchedule_UnknownSchedule_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await DeactivateScheduleAsync(TestAccessTokens.CreateOperator(operatorId), Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task DeactivateSchedule_AnotherOperatorsSchedule_IsReportedNotFoundAndLeftActive()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        Guid scheduleId = await SeedScheduleAsync(owner, "Active");

        // Act
        using HttpResponseMessage response = await DeactivateScheduleAsync(TestAccessTokens.CreateOperator(otherOperator), scheduleId);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal("Active", await StatusAsync(scheduleId));
        Assert.Equal("0 - -", await _database.QueryScalarAsync<string>(EventsSql, new { ScheduleId = scheduleId }));
    }

    [Fact]
    public async Task DeactivateSchedule_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await DeactivateScheduleAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task DeactivateSchedule_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await DeactivateScheduleAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task DeactivateSchedule_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/schedules/{Guid.NewGuid()}/deactivate", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Guid> SeedScheduleAsync(Guid operatorId, string status)
    {
        var scheduleId = Guid.NewGuid();
        await _database.SeedAsync(InsertScheduleSql, new
        {
            ScheduleId = scheduleId,
            BusId = Guid.NewGuid(),
            RouteId = Guid.NewGuid(),
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "TD" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            Status = status,
            Mumbai = _mumbai,
            Pune = _pune,
            Departures = _departures,
            Statuses = _statuses,
        });
        return scheduleId;
    }

    private async Task<string?> StatusAsync(Guid scheduleId)
    {
        return await _database.QueryScalarAsync<string>(StatusSql, new { ScheduleId = scheduleId });
    }

    private async Task<HttpResponseMessage> DeactivateScheduleAsync(string accessToken, Guid scheduleId)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(_now));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, $"/schedules/{scheduleId}/deactivate", accessToken);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "deactivate-test-correlation");
        return await client.SendAsync(request);
    }
}
