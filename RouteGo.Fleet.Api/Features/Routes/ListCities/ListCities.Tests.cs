using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Routes.ListCities;

[Collection(DatabaseCollection.Name)]
public sealed class ListCitiesTests
{
    private const string Route = "/cities";

    private static readonly string[] _seededNamesInOrder = new[]
    {
        "Ahmedabad", "Aurangabad", "Belagavi", "Bengaluru", "Hubballi", "Hyderabad", "Indore",
        "Kolhapur", "Lonavala", "Mangaluru", "Mumbai", "Nagpur", "Nashik", "Panaji", "Pune",
        "Satara", "Shirdi", "Solapur", "Surat", "Vadodara",
    };

    private readonly DatabaseFixture _database;

    public ListCitiesTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task ListCities_AnySignedInRole_ReturnsAllSeededCitiesSortedByName(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using JsonDocument body = await ListCitiesAsync(accessToken);

        // Assert
        JsonElement items = body.RootElement.GetProperty("items");
        JsonElement.ArrayEnumerator cities = items.EnumerateArray();
        string?[] names = cities.Select(city => city.GetProperty("name").GetString()).ToArray();
        Assert.Equal(_seededNamesInOrder, names);
        Assert.All(items.EnumerateArray(), city => Assert.NotEqual(Guid.Empty, city.GetProperty("cityId").GetGuid()));
        Assert.Contains(items.EnumerateArray(), city => IsCity(city, "Pune", "Maharashtra"));
        Assert.Contains(items.EnumerateArray(), city => IsCity(city, "Bengaluru", "Karnataka"));
    }

    [Fact]
    public async Task ListCities_OperatorWithoutProfile_ReturnsTheList()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using JsonDocument body = await ListCitiesAsync(accessToken);

        // Assert
        Assert.Equal(_seededNamesInOrder.Length, body.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task ListCities_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync(Route);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ListCities_TokenSignedByUntrustedKey_IsRefusedUnauthorized()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateWithUntrustedKey("Customer");
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, Route, accessToken);

        // Act
        using HttpResponseMessage response = await client.SendAsync(request);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static bool IsCity(JsonElement city, string name, string state)
    {
        bool isNamed = city.GetProperty("name").GetString() == name;
        bool isInState = city.GetProperty("state").GetString() == state;
        return isNamed && isInState;
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<JsonDocument> ListCitiesAsync(string accessToken)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, Route, accessToken);
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
