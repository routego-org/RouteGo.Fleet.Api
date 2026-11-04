using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Routes.ListRoutes;

[Collection(DatabaseCollection.Name)]
public sealed class ListRoutesTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _lonavala = Guid.Parse("ef669cf3-be1f-491d-9c86-7395ecb67f75");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");
    private static readonly Guid _nashik = Guid.Parse("56e2c262-b688-4bab-81bb-515b1bb8af35");
    private static readonly Guid _panaji = Guid.Parse("ffcacdfe-153a-4a20-9606-e04c12f02994");

    private const string InsertRouteSql = """
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status, deactivated_at)
        VALUES (@RouteId, @OperatorId, 150, @Status, CASE WHEN @Status = 'Inactive' THEN now() END);
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Source, 0), (@RouteId, 2, @Destination, 210);
        """;

    private readonly DatabaseFixture _database;

    public ListRoutesTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ListRoutes_ActiveAndInactiveOwnRoutes_ReturnsBothButNotAnotherOperatorsRoute()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        await SeedRouteAsync(operatorId, _mumbai, _pune, "Active");
        await SeedRouteAsync(operatorId, _pune, _nashik, "Inactive");
        await SeedRouteAsync(otherOperator, _mumbai, _panaji, "Active");

        // Act
        using JsonDocument page = await ListAsync(operatorId, string.Empty);

        // Assert
        JsonElement items = page.RootElement.GetProperty("items");
        Assert.Equal(new[] { "Mumbai → Pune", "Pune → Nashik" }, Journeys(items));
        Assert.Equal(new[] { "active", "inactive" }, Statuses(items));
        Assert.Equal(2, page.RootElement.GetProperty("totalCount").GetInt32());
        JsonElement first = items[0];
        Assert.Equal(new[] { "Mumbai", "Pune" }, CityNames(first.GetProperty("stops")));
        Assert.Equal(150, first.GetProperty("distanceKm").GetInt32());
        Assert.Equal(210, first.GetProperty("journeyMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("deactivatedAt").ValueKind);
        Assert.Equal(JsonValueKind.String, items[1].GetProperty("deactivatedAt").ValueKind);
    }

    [Fact]
    public async Task ListRoutes_StatusActive_ReturnsOnlyTheActiveRoute()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        await SeedRouteAsync(operatorId, _mumbai, _pune, "Active");
        await SeedRouteAsync(operatorId, _pune, _nashik, "Inactive");

        // Act
        using JsonDocument page = await ListAsync(operatorId, "?status=active");

        // Assert
        Assert.Equal(new[] { "Mumbai → Pune" }, Journeys(page.RootElement.GetProperty("items")));
        Assert.Equal(1, page.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ListRoutes_NoRoutesYet_ReturnsAnEmptyPageWithTotalCountZero()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using JsonDocument page = await ListAsync(operatorId, string.Empty);

        // Assert
        Assert.Equal(0, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(0, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(20, page.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData(1, new[] { "Mumbai → Lonavala", "Mumbai → Pune" })]
    [InlineData(2, new[] { "Pune → Nashik" })]
    public async Task ListRoutes_PagesOfTwo_AreSortedBySourceThenDestination(int pageNumber, string[] expectedJourneys)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        await SeedRouteAsync(operatorId, _pune, _nashik, "Active");
        await SeedRouteAsync(operatorId, _mumbai, _pune, "Active");
        await SeedRouteAsync(operatorId, _mumbai, _lonavala, "Active");

        // Act
        using JsonDocument page = await ListAsync(operatorId, $"?page={pageNumber}&pageSize=2");

        // Assert
        Assert.Equal(expectedJourneys, Journeys(page.RootElement.GetProperty("items")));
        Assert.Equal(3, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(pageNumber, page.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(2, page.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("?status=closed", "status")]
    [InlineData("?status=Active", "status")]
    [InlineData("?page=0", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=201", "pageSize")]
    public async Task ListRoutes_InvalidQuery_IsRefusedValidationFailedNamingTheField(string query, string field)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await SendListAsync(TestAccessTokens.CreateOperator(operatorId), query);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        IEnumerable<string> fields = problem.RootElement.GetProperty("errors").EnumerateObject().Select(error => error.Name);
        Assert.Contains(field, fields);
    }

    [Fact]
    public async Task ListRoutes_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await SendListAsync(accessToken, string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task ListRoutes_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await SendListAsync(accessToken, string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ListRoutes_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/routes");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static string[] Journeys(JsonElement items)
    {
        JsonElement.ArrayEnumerator routes = items.EnumerateArray();
        return routes.Select(route => $"{CityName(route, "source")} → {CityName(route, "destination")}").ToArray();
    }

    private static string? CityName(JsonElement route, string end)
    {
        return route.GetProperty(end).GetProperty("name").GetString();
    }

    private static string?[] Statuses(JsonElement items)
    {
        JsonElement.ArrayEnumerator routes = items.EnumerateArray();
        return routes.Select(route => route.GetProperty("status").GetString()).ToArray();
    }

    private static string?[] CityNames(JsonElement stops)
    {
        JsonElement.ArrayEnumerator items = stops.EnumerateArray();
        return items.Select(stop => stop.GetProperty("city").GetProperty("name").GetString()).ToArray();
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task SeedRouteAsync(Guid operatorId, Guid source, Guid destination, string status)
    {
        await _database.SeedAsync(InsertRouteSql, new
        {
            RouteId = Guid.NewGuid(),
            OperatorId = operatorId.ToString(),
            Status = status,
            Source = source,
            Destination = destination,
        });
    }

    private async Task<JsonDocument> ListAsync(Guid operatorId, string query)
    {
        using HttpResponseMessage response = await SendListAsync(TestAccessTokens.CreateOperator(operatorId), query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SendListAsync(string accessToken, string query)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, $"/routes{query}", accessToken);
        return await client.SendAsync(request);
    }
}
