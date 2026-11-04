using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Buses.DeactivateBus;

[Collection(DatabaseCollection.Name)]
public sealed class DeactivateBusTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 10, 8, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _departedAt = new DateTimeOffset(2026, 10, 9, 16, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _nextDepartureAt = new DateTimeOffset(2026, 10, 10, 16, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _secondDepartureAt = new DateTimeOffset(2026, 10, 11, 16, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _thirdDepartureAt = new DateTimeOffset(2026, 10, 12, 16, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _cancelledDepartureAt = new DateTimeOffset(2026, 10, 13, 16, 30, 0, TimeSpan.Zero);

    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Seater', true, NULL, @Status);
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 10, 4, 2);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Seater', 1);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'S1', 'Lower', 1, 1, 'Seater'), (@BusId, 'S2', 'Lower', 1, 2, 'Seater');
        """;

    // A route and schedule for the bus, then one trip per departure. A cancelled trip carries the
    // cause, reason and time the trips table requires of every cancelled trip.
    private const string InsertTripsSql = """
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, 'Active');
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Pune, 210);
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '22:00', ARRAY['Monday'], 'Active', '2026-12-31');
        INSERT INTO trips.trips (schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status,
                                 cancellation_cause, cancel_reason, cancelled_at)
        SELECT @ScheduleId, @OperatorId, @RouteId, @BusId, (t.departure_at AT TIME ZONE 'Asia/Kolkata')::date,
               t.departure_at, t.departure_at + interval '210 minutes', t.status,
               CASE WHEN t.status = 'Cancelled' THEN 'Operator' END,
               CASE WHEN t.status = 'Cancelled' THEN 'Bus sent for repair' END,
               CASE WHEN t.status = 'Cancelled' THEN t.departure_at - interval '1 day' END
        FROM unnest(@Departures, @Statuses) AS t(departure_at, status);
        """;

    private const string SelectStatusSql = """
        SELECT status FROM buses.buses WHERE bus_id = @BusId
        """;

    private readonly DatabaseFixture _database;

    public DeactivateBusTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task DeactivateBus_ActiveBusWithoutTrips_BecomesInactive()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId, "Active");

        // Act
        using HttpResponseMessage response = await DeactivateBusAsync(TestAccessTokens.CreateOperator(operatorId), busId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bus = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(busId, bus.RootElement.GetProperty("busId").GetGuid());
        Assert.Equal("inactive", bus.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, bus.RootElement.GetProperty("totalSeats").GetInt32());
        Assert.Equal("Inactive", await StoredStatusAsync(busId));
    }

    [Fact]
    public async Task DeactivateBus_OnlyDepartedAndCancelledTrips_BecomesInactive()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId, "Active");
        await SeedTripsAsync(operatorId, busId,
            new[] { _departedAt, _cancelledDepartureAt },
            new[] { "Scheduled", "Cancelled" });

        // Act
        using HttpResponseMessage response = await DeactivateBusAsync(TestAccessTokens.CreateOperator(operatorId), busId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Inactive", await StoredStatusAsync(busId));
    }

    [Fact]
    public async Task DeactivateBus_ThreeFutureScheduledTrips_IsRefusedBusInUseAndStaysActive()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId, "Active");
        await SeedTripsAsync(operatorId, busId,
            new[] { _departedAt, _thirdDepartureAt, _nextDepartureAt, _secondDepartureAt, _cancelledDepartureAt },
            new[] { "Scheduled", "Scheduled", "Scheduled", "Scheduled", "Cancelled" });

        // Act
        using HttpResponseMessage response = await DeactivateBusAsync(TestAccessTokens.CreateOperator(operatorId), busId);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BUS_IN_USE", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(3, problem.RootElement.GetProperty("futureTripCount").GetInt32());
        Assert.Equal(_nextDepartureAt, problem.RootElement.GetProperty("nextDepartureAt").GetDateTimeOffset());
        Assert.Equal("Active", await StoredStatusAsync(busId));
    }

    [Fact]
    public async Task DeactivateBus_AlreadyInactive_ReturnsBusUnchanged()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId, "Inactive");

        // Act
        using HttpResponseMessage response = await DeactivateBusAsync(TestAccessTokens.CreateOperator(operatorId), busId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bus = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("inactive", bus.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DeactivateBus_UnknownBus_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await DeactivateBusAsync(TestAccessTokens.CreateOperator(operatorId), Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task DeactivateBus_AnotherOperatorsBus_IsReportedNotFoundAndLeftActive()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(owner, "Active");

        // Act
        using HttpResponseMessage response = await DeactivateBusAsync(TestAccessTokens.CreateOperator(otherOperator), busId);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal("Active", await StoredStatusAsync(busId));
    }

    [Fact]
    public async Task DeactivateBus_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await DeactivateBusAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task DeactivateBus_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await DeactivateBusAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task DeactivateBus_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/buses/{Guid.NewGuid()}/deactivate", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Guid> SeedBusAsync(Guid operatorId, string status)
    {
        var busId = Guid.NewGuid();
        await _database.SeedAsync(InsertBusSql, new
        {
            BusId = busId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "T" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant(),
            Status = status,
        });
        return busId;
    }

    private async Task SeedTripsAsync(Guid operatorId, Guid busId, DateTimeOffset[] departures, string[] statuses)
    {
        await _database.SeedAsync(InsertTripsSql, new
        {
            RouteId = Guid.NewGuid(),
            ScheduleId = Guid.NewGuid(),
            OperatorId = operatorId.ToString(),
            BusId = busId,
            Mumbai = _mumbai,
            Pune = _pune,
            Departures = departures,
            Statuses = statuses,
        });
    }

    private async Task<string?> StoredStatusAsync(Guid busId)
    {
        return await _database.QueryScalarAsync<string>(SelectStatusSql, new { BusId = busId });
    }

    private async Task<HttpResponseMessage> DeactivateBusAsync(string accessToken, Guid busId)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(_now));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, $"/buses/{busId}/deactivate", accessToken);
        return await client.SendAsync(request);
    }
}
