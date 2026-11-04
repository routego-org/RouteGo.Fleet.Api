using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Trips.ActivateSchedule;

[Collection(DatabaseCollection.Name)]
public sealed class ActivateScheduleTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    // The bus and route take the given status too: activation has no precondition on either.
    // Fares are stored in paise.
    private const string InsertScheduleSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Seater', true, NULL, @PartsStatus);
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 5, 2, 1);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Seater', 1);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'S1', 'Lower', 1, 1, 'Seater');
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, @PartsStatus);
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Pune, 210);
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '22:30', ARRAY['Monday'], @Status, '2026-10-22');
        INSERT INTO trips.schedule_class_fares (schedule_id, seat_class, fare)
        VALUES (@ScheduleId, 'Seater', 45000);
        """;

    private const string StoredStateSql = """
        SELECT s.status || ' ' || (SELECT count(*) FROM trips.trips t WHERE t.schedule_id = s.schedule_id) || ' trips'
        FROM trips.schedules s WHERE s.schedule_id = @ScheduleId
        """;

    private const string UpdatedAtSql = """
        SELECT updated_at FROM trips.schedules WHERE schedule_id = @ScheduleId
        """;

    private readonly DatabaseFixture _database;

    public ActivateScheduleTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ActivateSchedule_InactiveSchedule_BecomesActiveAndGeneratesNoTrips()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid scheduleId = await SeedScheduleAsync(operatorId, "Inactive", "Active");

        // Act
        using HttpResponseMessage response = await ActivateScheduleAsync(TestAccessTokens.CreateOperator(operatorId), scheduleId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var schedule = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(scheduleId, schedule.RootElement.GetProperty("scheduleId").GetGuid());
        Assert.Equal("active", schedule.RootElement.GetProperty("status").GetString());
        Assert.Equal("[{\"seatClass\":\"Seater\",\"fare\":450}]", schedule.RootElement.GetProperty("classFares").GetRawText());
        Assert.Equal("2026-10-22", schedule.RootElement.GetProperty("tripsGeneratedThrough").GetString());
        Assert.Equal("Active 0 trips", await StoredStateAsync(scheduleId));
    }

    [Fact]
    public async Task ActivateSchedule_InactiveBusAndRoute_StillBecomesActive()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid scheduleId = await SeedScheduleAsync(operatorId, "Inactive", "Inactive");

        // Act
        using HttpResponseMessage response = await ActivateScheduleAsync(TestAccessTokens.CreateOperator(operatorId), scheduleId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Active 0 trips", await StoredStateAsync(scheduleId));
    }

    [Fact]
    public async Task ActivateSchedule_AlreadyActive_ReturnsScheduleUnchanged()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid scheduleId = await SeedScheduleAsync(operatorId, "Active", "Active");
        DateTime updatedBefore = await _database.QueryScalarAsync<DateTime>(UpdatedAtSql, new { ScheduleId = scheduleId });

        // Act
        using HttpResponseMessage response = await ActivateScheduleAsync(TestAccessTokens.CreateOperator(operatorId), scheduleId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var schedule = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("active", schedule.RootElement.GetProperty("status").GetString());
        Assert.Equal(updatedBefore, await _database.QueryScalarAsync<DateTime>(UpdatedAtSql, new { ScheduleId = scheduleId }));
    }

    [Fact]
    public async Task ActivateSchedule_UnknownSchedule_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await ActivateScheduleAsync(TestAccessTokens.CreateOperator(operatorId), Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ActivateSchedule_AnotherOperatorsSchedule_IsReportedNotFoundAndLeftInactive()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        Guid scheduleId = await SeedScheduleAsync(owner, "Inactive", "Active");

        // Act
        using HttpResponseMessage response = await ActivateScheduleAsync(TestAccessTokens.CreateOperator(otherOperator), scheduleId);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal("Inactive 0 trips", await StoredStateAsync(scheduleId));
    }

    [Fact]
    public async Task ActivateSchedule_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await ActivateScheduleAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task ActivateSchedule_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await ActivateScheduleAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ActivateSchedule_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/schedules/{Guid.NewGuid()}/activate", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Guid> SeedScheduleAsync(Guid operatorId, string status, string partsStatus)
    {
        var scheduleId = Guid.NewGuid();
        await _database.SeedAsync(InsertScheduleSql, new
        {
            ScheduleId = scheduleId,
            BusId = Guid.NewGuid(),
            RouteId = Guid.NewGuid(),
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "TA" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            Status = status,
            PartsStatus = partsStatus,
            Mumbai = _mumbai,
            Pune = _pune,
        });
        return scheduleId;
    }

    private async Task<string?> StoredStateAsync(Guid scheduleId)
    {
        return await _database.QueryScalarAsync<string>(StoredStateSql, new { ScheduleId = scheduleId });
    }

    private async Task<HttpResponseMessage> ActivateScheduleAsync(string accessToken, Guid scheduleId)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, $"/schedules/{scheduleId}/activate", accessToken);
        return await client.SendAsync(request);
    }
}
