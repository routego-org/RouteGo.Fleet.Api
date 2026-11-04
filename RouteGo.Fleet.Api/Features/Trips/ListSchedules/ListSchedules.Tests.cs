using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Trips.ListSchedules;

[Collection(DatabaseCollection.Name)]
public sealed class ListSchedulesTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Seater', true, NULL, 'Active');
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 5, 2, 1);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Seater', 1);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'S1', 'Lower', 1, 1, 'Seater');
        """;

    private const string InsertRouteSql = """
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, 'Active');
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Pune, 210);
        """;

    // Fares are stored in paise.
    private const string InsertScheduleSql = """
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, @DepartureTime, ARRAY['Monday', 'Friday'], @Status, '2026-10-22');
        INSERT INTO trips.schedule_class_fares (schedule_id, seat_class, fare)
        VALUES (@ScheduleId, 'Seater', 45000);
        """;

    private readonly DatabaseFixture _database;

    public ListSchedulesTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ListSchedules_ActiveAndInactiveOwnSchedules_ReturnsBothButNotAnotherOperatorsSchedule()
    {
        // Arrange
        Operator operatorOne = await SeedOperatorAsync();
        Operator operatorTwo = await SeedOperatorAsync();
        Guid active = await SeedScheduleAsync(operatorOne.RouteOne, "22:30", "Active");
        Guid inactive = await SeedScheduleAsync(operatorOne.RouteTwo, "06:00", "Inactive");
        await SeedScheduleAsync(operatorTwo.RouteOne, "10:00", "Active");

        // Act
        using JsonDocument page = await ListAsync(operatorOne.OperatorId, string.Empty);

        // Assert
        JsonElement items = page.RootElement.GetProperty("items");
        Assert.Equal(2, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(inactive, items[0].GetProperty("scheduleId").GetGuid());
        Assert.Equal(active, items[1].GetProperty("scheduleId").GetGuid());
        Assert.Equal("inactive", items[0].GetProperty("status").GetString());
        Assert.Equal("[\"monday\",\"friday\"]", items[1].GetProperty("daysOfWeek").GetRawText());
        Assert.Equal("[{\"seatClass\":\"Seater\",\"fare\":450}]", items[1].GetProperty("classFares").GetRawText());
        Assert.Equal("2026-10-22", items[1].GetProperty("tripsGeneratedThrough").GetString());
        Assert.Equal("22:30", items[1].GetProperty("departureTime").GetString());
    }

    [Fact]
    public async Task ListSchedules_StatusInactive_ReturnsOnlyTheInactiveSchedule()
    {
        // Arrange
        Operator owner = await SeedOperatorAsync();
        await SeedScheduleAsync(owner.RouteOne, "22:30", "Active");
        Guid inactive = await SeedScheduleAsync(owner.RouteTwo, "06:00", "Inactive");

        // Act
        using JsonDocument page = await ListAsync(owner.OperatorId, "?status=inactive");

        // Assert
        Assert.Equal(1, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(inactive, page.RootElement.GetProperty("items")[0].GetProperty("scheduleId").GetGuid());
    }

    [Fact]
    public async Task ListSchedules_ByRoute_ReturnsOnlyThatRoutesSchedules()
    {
        // Arrange
        Operator owner = await SeedOperatorAsync();
        Guid onRouteOne = await SeedScheduleAsync(owner.RouteOne, "22:30", "Active");
        await SeedScheduleAsync(owner.RouteTwo, "06:00", "Inactive");

        // Act
        using JsonDocument page = await ListAsync(owner.OperatorId, $"?routeId={owner.RouteOne.RouteId}");

        // Assert
        Assert.Equal(1, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(onRouteOne, page.RootElement.GetProperty("items")[0].GetProperty("scheduleId").GetGuid());
    }

    [Fact]
    public async Task ListSchedules_ByBus_ReturnsOnlyThatBussSchedules()
    {
        // Arrange
        Operator owner = await SeedOperatorAsync();
        await SeedScheduleAsync(owner.RouteOne, "22:30", "Active");
        Guid onBusTwo = await SeedScheduleAsync(owner.RouteTwo, "06:00", "Inactive");

        // Act
        using JsonDocument page = await ListAsync(owner.OperatorId, $"?busId={owner.RouteTwo.BusId}");

        // Assert
        Assert.Equal(1, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(onBusTwo, page.RootElement.GetProperty("items")[0].GetProperty("scheduleId").GetGuid());
    }

    [Fact]
    public async Task ListSchedules_NoSchedulesYet_ReturnsAnEmptyPageWithTotalCountZero()
    {
        // Arrange
        Operator owner = await SeedOperatorAsync();

        // Act
        using JsonDocument page = await ListAsync(owner.OperatorId, string.Empty);

        // Assert
        Assert.Equal(0, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(1, page.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(20, page.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task ListSchedules_PagesOfTwo_AreSortedByDepartureTime()
    {
        // Arrange
        Operator owner = await SeedOperatorAsync();
        Guid late = await SeedScheduleAsync(owner.RouteOne, "22:30", "Active");
        Guid early = await SeedScheduleAsync(owner.RouteOne, "06:00", "Active");
        Guid midday = await SeedScheduleAsync(owner.RouteTwo, "14:00", "Active");

        // Act
        using JsonDocument first = await ListAsync(owner.OperatorId, "?page=1&pageSize=2");
        using JsonDocument second = await ListAsync(owner.OperatorId, "?page=2&pageSize=2");

        // Assert
        Assert.Equal(3, first.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(early, first.RootElement.GetProperty("items")[0].GetProperty("scheduleId").GetGuid());
        Assert.Equal(midday, first.RootElement.GetProperty("items")[1].GetProperty("scheduleId").GetGuid());
        Assert.Equal(1, second.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(late, second.RootElement.GetProperty("items")[0].GetProperty("scheduleId").GetGuid());
    }

    [Theory]
    [InlineData("?status=Active", "status")]
    [InlineData("?page=0", "page")]
    [InlineData("?pageSize=201", "pageSize")]
    [InlineData("?routeId=not-a-uuid", "routeId")]
    [InlineData("?busId=12345", "busId")]
    public async Task ListSchedules_InvalidQuery_IsRefusedValidationFailedNamingTheField(string query, string field)
    {
        // Arrange
        Operator owner = await SeedOperatorAsync();

        // Act
        using HttpResponseMessage response = await SendAsync(TestAccessTokens.CreateOperator(owner.OperatorId), query);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"No error for {field}.");
    }

    [Fact]
    public async Task ListSchedules_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await SendAsync(accessToken, string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task ListSchedules_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await SendAsync(accessToken, string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ListSchedules_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/schedules");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    // An operator with a profile and two buses, each on its own route.
    private async Task<Operator> SeedOperatorAsync()
    {
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Owned routeOne = await SeedRouteAndBusAsync(operatorId);
        Owned routeTwo = await SeedRouteAndBusAsync(operatorId);
        return new Operator(operatorId, routeOne, routeTwo);
    }

    private async Task<Owned> SeedRouteAndBusAsync(Guid operatorId)
    {
        var owned = new Owned(operatorId.ToString(), Guid.NewGuid(), Guid.NewGuid());
        await _database.SeedAsync(InsertBusSql, new
        {
            owned.BusId,
            owned.OperatorId,
            RegistrationNumber = "TL" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
        });
        await _database.SeedAsync(InsertRouteSql, new { owned.RouteId, owned.OperatorId, Mumbai = _mumbai, Pune = _pune });
        return owned;
    }

    private async Task<Guid> SeedScheduleAsync(Owned owned, string departureTime, string status)
    {
        var scheduleId = Guid.NewGuid();
        await _database.SeedAsync(InsertScheduleSql, new
        {
            ScheduleId = scheduleId,
            owned.OperatorId,
            owned.RouteId,
            owned.BusId,
            DepartureTime = departureTime,
            Status = status,
        });
        return scheduleId;
    }

    private async Task<JsonDocument> ListAsync(Guid operatorId, string query)
    {
        using HttpResponseMessage response = await SendAsync(TestAccessTokens.CreateOperator(operatorId), query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SendAsync(string accessToken, string query)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, "/schedules" + query, accessToken);
        return await client.SendAsync(request);
    }

    /// <summary>A route and a bus owned by one operator.</summary>
    private sealed record Owned(string OperatorId, Guid RouteId, Guid BusId);

    /// <summary>An operator with two route-and-bus pairs.</summary>
    private sealed record Operator(Guid OperatorId, Owned RouteOne, Owned RouteTwo);
}
