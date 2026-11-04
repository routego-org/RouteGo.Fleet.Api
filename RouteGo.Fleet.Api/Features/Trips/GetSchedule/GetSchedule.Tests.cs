using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Trips.GetSchedule;

[Collection(DatabaseCollection.Name)]
public sealed class GetScheduleTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    // The bus lists "Upper berth" before "Lower berth", so the fares come back in that order rather
    // than alphabetically. Fares are stored in paise.
    private const string InsertScheduleSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Sleeper', true, NULL, 'Active');
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 5, 2, 1);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Upper berth', 1), (@BusId, 'Lower berth', 2);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'U1', 'Lower', 1, 1, 'Upper berth'), (@BusId, 'L1', 'Lower', 1, 2, 'Lower berth');
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, 'Active');
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Pune, 210);
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '22:30', ARRAY['Monday', 'Wednesday', 'Friday'], 'Active', '2026-10-22');
        INSERT INTO trips.schedule_class_fares (schedule_id, seat_class, fare)
        VALUES (@ScheduleId, 'Lower berth', 120000), (@ScheduleId, 'Upper berth', 100000);
        """;

    private readonly DatabaseFixture _database;

    public GetScheduleTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task GetSchedule_OwnSchedule_ReturnsEveryDetail()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Seeded seeded = await SeedScheduleAsync(operatorId);

        // Act
        using HttpResponseMessage response = await GetScheduleAsync(TestAccessTokens.CreateOperator(operatorId), seeded.ScheduleId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var schedule = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = schedule.RootElement;
        Assert.Equal(seeded.ScheduleId, root.GetProperty("scheduleId").GetGuid());
        Assert.Equal(operatorId.ToString(), root.GetProperty("operatorId").GetString());
        Assert.Equal(seeded.RouteId, root.GetProperty("routeId").GetGuid());
        Assert.Equal(seeded.BusId, root.GetProperty("busId").GetGuid());
        Assert.Equal("22:30", root.GetProperty("departureTime").GetString());
        Assert.Equal("[\"monday\",\"wednesday\",\"friday\"]", root.GetProperty("daysOfWeek").GetRawText());
        Assert.Equal("active", root.GetProperty("status").GetString());
        Assert.Equal("2026-10-22", root.GetProperty("tripsGeneratedThrough").GetString());
        Assert.True(root.TryGetProperty("createdAt", out _));
        Assert.True(root.TryGetProperty("updatedAt", out _));
    }

    [Fact]
    public async Task GetSchedule_OwnSchedule_ReturnsFaresInRupeesInTheBusSeatClassOrder()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Seeded seeded = await SeedScheduleAsync(operatorId);

        // Act
        using HttpResponseMessage response = await GetScheduleAsync(TestAccessTokens.CreateOperator(operatorId), seeded.ScheduleId);

        // Assert
        using var schedule = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "[{\"seatClass\":\"Upper berth\",\"fare\":1000},{\"seatClass\":\"Lower berth\",\"fare\":1200}]",
            schedule.RootElement.GetProperty("classFares").GetRawText());
    }

    [Fact]
    public async Task GetSchedule_UnknownSchedule_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await GetScheduleAsync(TestAccessTokens.CreateOperator(operatorId), Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetSchedule_AnotherOperatorsSchedule_IsReportedNotFound()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        Seeded seeded = await SeedScheduleAsync(owner);

        // Act
        using HttpResponseMessage response = await GetScheduleAsync(TestAccessTokens.CreateOperator(otherOperator), seeded.ScheduleId);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetSchedule_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await GetScheduleAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task GetSchedule_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await GetScheduleAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetSchedule_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync($"/schedules/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Seeded> SeedScheduleAsync(Guid operatorId)
    {
        var seeded = new Seeded(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _database.SeedAsync(InsertScheduleSql, new
        {
            seeded.ScheduleId,
            seeded.BusId,
            seeded.RouteId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "TG" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            Mumbai = _mumbai,
            Pune = _pune,
        });
        return seeded;
    }

    private async Task<HttpResponseMessage> GetScheduleAsync(string accessToken, Guid scheduleId)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, $"/schedules/{scheduleId}", accessToken);
        return await client.SendAsync(request);
    }

    private sealed record Seeded(Guid ScheduleId, Guid BusId, Guid RouteId);
}
