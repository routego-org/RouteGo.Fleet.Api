using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouteGo.Fleet.Api.Features.Routes.CreateRoute;

[Collection(DatabaseCollection.Name)]
public sealed class CreateRouteTests
{
    private const string Route = "/routes";

    // Fixed by the city seed migration, identical in every environment.
    private const string Mumbai = "ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37";
    private const string Lonavala = "ef669cf3-be1f-491d-9c86-7395ecb67f75";
    private const string Pune = "0484575c-a368-4057-896d-c99d78ba9cce";

    private const string CountOperatorRoutesSql = "SELECT count(*) FROM routes.routes WHERE operator_id = @OperatorId";

    private readonly DatabaseFixture _database;

    public CreateRouteTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task CreateRoute_MumbaiLonavalaPune_CreatesActiveRouteOwnedByTheOperator()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await CreateRouteAsync(TestAccessTokens.CreateOperator(operatorId), MumbaiToPune());

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var route = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid routeId = route.RootElement.GetProperty("routeId").GetGuid();
        Assert.Equal($"/routes/{routeId}", response.Headers.Location!.OriginalString);
        Assert.Equal("active", route.RootElement.GetProperty("status").GetString());
        Assert.Equal(operatorId.ToString(), route.RootElement.GetProperty("operatorId").GetString());
        Assert.Equal("Mumbai", route.RootElement.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal("Pune", route.RootElement.GetProperty("destination").GetProperty("name").GetString());
        Assert.Equal(210, route.RootElement.GetProperty("journeyMinutes").GetInt32());
        Assert.Equal(150, route.RootElement.GetProperty("distanceKm").GetInt32());
        Assert.Equal(JsonValueKind.Null, route.RootElement.GetProperty("deactivatedAt").ValueKind);
        Assert.Equal(3L, await _database.QueryScalarAsync<long>(
            "SELECT count(*) FROM routes.route_stops WHERE route_id = @RouteId", new { RouteId = routeId }));
    }

    [Theory]
    [InlineData("only one stop", "stops")]
    [InlineData("13 stops", "stops")]
    [InlineData("first stop's offset is 15", "stops[0].offsetMinutes")]
    [InlineData("offset equal to the previous stop's", "stops[2].offsetMinutes")]
    [InlineData("city appears twice", "stops[2].cityId")]
    [InlineData("destination offset of 2,881", "stops[2].offsetMinutes")]
    [InlineData("distance of 0 km", "distanceKm")]
    [InlineData("distance of 3,001 km", "distanceKm")]
    [InlineData("distance of 150.5 km", "distanceKm")]
    public async Task CreateRoute_StopsOrDistanceBreakARule_IsRefusedNamingTheFieldAndCreatesNothing(string scenario, string field)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        JsonObject body = InvalidBody(scenario);

        // Act
        using HttpResponseMessage response = await CreateRouteAsync(TestAccessTokens.CreateOperator(operatorId), body);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        IEnumerable<string> fields = problem.RootElement.GetProperty("errors").EnumerateObject().Select(error => error.Name);
        Assert.Contains(fields, name => name.Contains(field, StringComparison.Ordinal));
        Assert.Equal(0L, await _database.QueryScalarAsync<long>(CountOperatorRoutesSql, new { OperatorId = operatorId.ToString() }));
    }

    [Fact]
    public async Task CreateRoute_CityNotInTheList_IsRefusedUnknownCityCarryingItsId()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        var unknownCityId = Guid.NewGuid();
        JsonObject body = MumbaiToPune();
        body["stops"]![1]!["cityId"] = unknownCityId.ToString();

        // Act
        using HttpResponseMessage response = await CreateRouteAsync(TestAccessTokens.CreateOperator(operatorId), body);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UNKNOWN_CITY", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(unknownCityId, problem.RootElement.GetProperty("cityId").GetGuid());
        Assert.Equal(0L, await _database.QueryScalarAsync<long>(CountOperatorRoutesSql, new { OperatorId = operatorId.ToString() }));
    }

    [Fact]
    public async Task CreateRoute_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await CreateRouteAsync(accessToken, MumbaiToPune());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task CreateRoute_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await CreateRouteAsync(accessToken, MumbaiToPune());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CreateRoute_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(Route, MumbaiToPune());

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    // Mumbai at 0, Lonavala at 110 and Pune at 210 minutes, 150 km — the criterion's route.
    private static JsonObject MumbaiToPune()
    {
        return new JsonObject
        {
            ["stops"] = new JsonArray(Stop(Mumbai, 0), Stop(Lonavala, 110), Stop(Pune, 210)),
            ["distanceKm"] = 150,
        };
    }

    private static JsonObject InvalidBody(string scenario)
    {
        JsonObject body = MumbaiToPune();
        JsonArray stops = body["stops"]!.AsArray();
        switch (scenario)
        {
            case "only one stop":
                body["stops"] = new JsonArray(Stop(Mumbai, 0));
                break;
            case "13 stops":
                body["stops"] = ThirteenStops();
                break;
            case "first stop's offset is 15":
                stops[0]!["offsetMinutes"] = 15;
                break;
            case "offset equal to the previous stop's":
                stops[2]!["offsetMinutes"] = 110;
                break;
            case "city appears twice":
                stops[2]!["cityId"] = Mumbai;
                break;
            case "destination offset of 2,881":
                stops[2]!["offsetMinutes"] = 2881;
                break;
            case "distance of 0 km":
                body["distanceKm"] = 0;
                break;
            case "distance of 3,001 km":
                body["distanceKm"] = 3001;
                break;
            case "distance of 150.5 km":
                body["distanceKm"] = 150.5;
                break;
            default:
                throw new ArgumentException($"Unknown scenario '{scenario}'.", nameof(scenario));
        }

        return body;
    }

    // Thirteen distinct cities from the seeded list, so only the count breaks a rule.
    private static JsonArray ThirteenStops()
    {
        string[] cityIds = new[]
        {
            "4e792207-c455-49f7-89d5-bdd56eb46fa9", "ba3b9ccd-316c-4506-be34-930461989820",
            "b85567d2-ef6f-412e-acad-f2d7865537cd", "d71b7577-bfbd-4e33-b2c9-9b2a5cba16fe",
            "06220551-2a5d-484b-8e96-4e86963cea0f", "bcb45c60-27fc-4f0b-8f01-60ab6717681a",
            "8892a678-627b-4e9b-84bf-00790132e903", "0256b37a-944f-45ab-8954-3521d54efad5",
            Lonavala, "9822ce8c-2354-4913-9b17-854ea1f6aca8", Mumbai,
            "ece48a00-91a9-42d9-b3c5-a6d81b05b55d", Pune,
        };
        var stops = new JsonArray();
        foreach (string cityId in cityIds)
        {
            stops.Add(Stop(cityId, stops.Count * 60));
        }

        return stops;
    }

    private static JsonObject Stop(string cityId, int offsetMinutes)
    {
        return new JsonObject { ["cityId"] = cityId, ["offsetMinutes"] = offsetMinutes };
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<HttpResponseMessage> CreateRouteAsync(string accessToken, JsonObject body)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, Route, accessToken, body);
        return await client.SendAsync(request);
    }
}
