using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Routes.DeactivateRoute;

[Collection(DatabaseCollection.Name)]
public sealed class DeactivateRouteTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _lonavala = Guid.Parse("ef669cf3-be1f-491d-9c86-7395ecb67f75");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 10, 8, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _firstDeactivatedAt = new DateTimeOffset(2026, 10, 1, 6, 0, 0, TimeSpan.Zero);

    private const string InsertRouteSql = """
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status, deactivated_at)
        VALUES (@RouteId, @OperatorId, 150, @Status, @DeactivatedAt);
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Lonavala, 110), (@RouteId, 3, @Pune, 210);
        """;

    private const string SelectStatusSql = """
        SELECT status FROM routes.routes WHERE route_id = @RouteId
        """;

    private readonly DatabaseFixture _database;

    public DeactivateRouteTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task DeactivateRoute_ActiveRoute_BecomesInactiveNow()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid routeId = await SeedMumbaiToPuneAsync(operatorId, "Active", null);

        // Act
        using HttpResponseMessage response = await DeactivateRouteAsync(TestAccessTokens.CreateOperator(operatorId), routeId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var route = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(routeId, route.RootElement.GetProperty("routeId").GetGuid());
        Assert.Equal("inactive", route.RootElement.GetProperty("status").GetString());
        Assert.Equal(_now, route.RootElement.GetProperty("deactivatedAt").GetDateTimeOffset());
        Assert.Equal(3, route.RootElement.GetProperty("stops").GetArrayLength());
        Assert.Equal("Inactive", await StoredStatusAsync(routeId));
    }

    [Fact]
    public async Task DeactivateRoute_AlreadyInactive_ReturnsRouteUnchanged()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid routeId = await SeedMumbaiToPuneAsync(operatorId, "Inactive", _firstDeactivatedAt);

        // Act
        using HttpResponseMessage response = await DeactivateRouteAsync(TestAccessTokens.CreateOperator(operatorId), routeId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var route = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("inactive", route.RootElement.GetProperty("status").GetString());
        Assert.Equal(_firstDeactivatedAt, route.RootElement.GetProperty("deactivatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task DeactivateRoute_UnknownRoute_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await DeactivateRouteAsync(TestAccessTokens.CreateOperator(operatorId), Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task DeactivateRoute_AnotherOperatorsRoute_IsReportedNotFoundAndLeftActive()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        Guid routeId = await SeedMumbaiToPuneAsync(owner, "Active", null);

        // Act
        using HttpResponseMessage response = await DeactivateRouteAsync(TestAccessTokens.CreateOperator(otherOperator), routeId);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal("Active", await StoredStatusAsync(routeId));
    }

    [Fact]
    public async Task DeactivateRoute_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await DeactivateRouteAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task DeactivateRoute_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await DeactivateRouteAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task DeactivateRoute_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/routes/{Guid.NewGuid()}/deactivate", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Guid> SeedMumbaiToPuneAsync(Guid operatorId, string status, DateTimeOffset? deactivatedAt)
    {
        var routeId = Guid.NewGuid();
        await _database.SeedAsync(InsertRouteSql, new
        {
            RouteId = routeId,
            OperatorId = operatorId.ToString(),
            Status = status,
            DeactivatedAt = deactivatedAt,
            Mumbai = _mumbai,
            Lonavala = _lonavala,
            Pune = _pune,
        });
        return routeId;
    }

    private async Task<string?> StoredStatusAsync(Guid routeId)
    {
        return await _database.QueryScalarAsync<string>(SelectStatusSql, new { RouteId = routeId });
    }

    private async Task<HttpResponseMessage> DeactivateRouteAsync(string accessToken, Guid routeId)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(_now));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, $"/routes/{routeId}/deactivate", accessToken);
        return await client.SendAsync(request);
    }
}
