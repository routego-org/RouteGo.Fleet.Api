using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Routes.GetRoute;

[Collection(DatabaseCollection.Name)]
public sealed class GetRouteTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _lonavala = Guid.Parse("ef669cf3-be1f-491d-9c86-7395ecb67f75");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    private const string InsertRouteSql = """
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, 'Active');
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Lonavala, 110), (@RouteId, 3, @Pune, 210);
        """;

    private readonly DatabaseFixture _database;

    public GetRouteTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task GetRoute_OwnRouteMumbaiLonavalaPune_ReturnsEveryStopInOrder()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid routeId = await SeedMumbaiToPuneAsync(operatorId);

        // Act
        using HttpResponseMessage response = await GetRouteAsync(TestAccessTokens.CreateOperator(operatorId), routeId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var route = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement stops = route.RootElement.GetProperty("stops");
        Assert.Equal(new[] { "Mumbai", "Lonavala", "Pune" }, CityNames(stops));
        Assert.Equal(new[] { 0, 110, 210 }, Offsets(stops));
        Assert.Equal("Mumbai", route.RootElement.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal("Pune", route.RootElement.GetProperty("destination").GetProperty("name").GetString());
        Assert.Equal(210, route.RootElement.GetProperty("journeyMinutes").GetInt32());
        Assert.Equal(150, route.RootElement.GetProperty("distanceKm").GetInt32());
        Assert.Equal("active", route.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetRoute_UnknownRoute_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await GetRouteAsync(TestAccessTokens.CreateOperator(operatorId), Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetRoute_AnotherOperatorsRoute_IsReportedNotFound()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        Guid routeId = await SeedMumbaiToPuneAsync(owner);

        // Act
        using HttpResponseMessage response = await GetRouteAsync(TestAccessTokens.CreateOperator(otherOperator), routeId);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetRoute_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await GetRouteAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task GetRoute_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await GetRouteAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetRoute_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync($"/routes/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static string?[] CityNames(JsonElement stops)
    {
        JsonElement.ArrayEnumerator items = stops.EnumerateArray();
        return items.Select(stop => stop.GetProperty("city").GetProperty("name").GetString()).ToArray();
    }

    private static int[] Offsets(JsonElement stops)
    {
        JsonElement.ArrayEnumerator items = stops.EnumerateArray();
        return items.Select(stop => stop.GetProperty("offsetMinutes").GetInt32()).ToArray();
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Guid> SeedMumbaiToPuneAsync(Guid operatorId)
    {
        var routeId = Guid.NewGuid();
        await _database.SeedAsync(InsertRouteSql, new
        {
            RouteId = routeId,
            OperatorId = operatorId.ToString(),
            Mumbai = _mumbai,
            Lonavala = _lonavala,
            Pune = _pune,
        });
        return routeId;
    }

    private async Task<HttpResponseMessage> GetRouteAsync(string accessToken, Guid routeId)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, $"/routes/{routeId}", accessToken);
        return await client.SendAsync(request);
    }
}
