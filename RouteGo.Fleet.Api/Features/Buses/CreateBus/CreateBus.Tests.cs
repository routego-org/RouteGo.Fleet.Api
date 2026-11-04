using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouteGo.Fleet.Api.Features.Buses.CreateBus;

[Collection(DatabaseCollection.Name)]
public sealed class CreateBusTests
{
    private const string Route = "/buses";

    private const string CountBusesSql = "SELECT count(*) FROM buses.buses WHERE registration_number = @RegistrationNumber";

    private readonly DatabaseFixture _database;

    public CreateBusTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task CreateBus_TwoDeckSleeper_CreatesActiveBusOwnedByTheOperator()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        JsonObject body = SleeperBody("MH-12 AB 1234");

        // Act
        using HttpResponseMessage response = await CreateBusAsync(TestAccessTokens.CreateOperator(operatorId), body);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var bus = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid busId = bus.RootElement.GetProperty("busId").GetGuid();
        Assert.Equal($"/buses/{busId}", response.Headers.Location!.OriginalString);
        Assert.Equal("MH12AB1234", bus.RootElement.GetProperty("registrationNumber").GetString());
        Assert.Equal(36, bus.RootElement.GetProperty("totalSeats").GetInt32());
        Assert.Equal("active", bus.RootElement.GetProperty("status").GetString());
        Assert.Equal(operatorId.ToString(), bus.RootElement.GetProperty("operatorId").GetString());
        Assert.Equal(36L, await _database.QueryScalarAsync<long>(
            "SELECT count(*) FROM buses.bus_seats WHERE bus_id = @BusId", new { BusId = busId }));
        Assert.Equal(operatorId.ToString(), await _database.QueryScalarAsync<string>(
            "SELECT created_by_id FROM buses.buses WHERE bus_id = @BusId", new { BusId = busId }));
    }

    [Fact]
    public async Task CreateBus_RegistrationAlreadyInRouteGo_IsRefusedAndNothingChanges()
    {
        // Arrange
        Guid firstOperator = await TestOperators.SeedProfileAsync(_database);
        Guid secondOperator = await TestOperators.SeedProfileAsync(_database);
        using HttpResponseMessage first = await CreateBusAsync(TestAccessTokens.CreateOperator(firstOperator), SmallBody("KA05MN4321"));

        // Act
        using HttpResponseMessage response = await CreateBusAsync(
            TestAccessTokens.CreateOperator(secondOperator), SmallBody("ka-05-mn-4321"));

        // Assert
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("BUS_REGISTRATION_ALREADY_EXISTS", await ProblemCodeAsync(response));
        Assert.Equal(1L, await _database.QueryScalarAsync<long>(CountBusesSql, new { RegistrationNumber = "KA05MN4321" }));
    }

    [Theory]
    [InlineData("label used twice ignoring case", "layout.seats[1].label")]
    [InlineData("seat outside the deck's rows", "layout.seats[0].row")]
    [InlineData("two seats in one place", "layout.seats[1].column")]
    [InlineData("seat class the bus lacks", "layout.seats[0].seatClass")]
    [InlineData("seat class with no seat", "seatClasses[1]")]
    [InlineData("61 seats", "layout.seats")]
    [InlineData("no lower deck", "layout.decks")]
    [InlineData("aisle after the last column", "layout.decks[0].aisleAfterColumn")]
    public async Task CreateBus_LayoutBreaksARule_IsRefusedNamingTheField(string scenario, string field)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        string registrationNumber = UniqueRegistrationNumber();
        JsonObject body = InvalidLayoutBody(scenario, registrationNumber);

        // Act
        using HttpResponseMessage response = await CreateBusAsync(TestAccessTokens.CreateOperator(operatorId), body);

        // Assert
        await AssertValidationFailedAsync(response, field);
        Assert.Equal(0L, await _database.QueryScalarAsync<long>(CountBusesSql, new { RegistrationNumber = registrationNumber }));
    }

    [Theory]
    [InlineData("registration number of 5 characters", "registrationNumber")]
    [InlineData("name of 1 character", "name")]
    [InlineData("6 seat classes", "seatClasses")]
    [InlineData("seat classes differing only in case", "seatClasses[1]")]
    [InlineData("amenity listed twice", "amenities")]
    [InlineData("amenity not in the list", "amenities")]
    public async Task CreateBus_FieldBreaksARule_IsRefusedNamingTheField(string scenario, string field)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        JsonObject body = InvalidFieldBody(scenario);

        // Act
        using HttpResponseMessage response = await CreateBusAsync(TestAccessTokens.CreateOperator(operatorId), body);

        // Assert
        await AssertValidationFailedAsync(response, field);
    }

    [Fact]
    public async Task CreateBus_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await CreateBusAsync(accessToken, SmallBody(UniqueRegistrationNumber()));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task CreateBus_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await CreateBusAsync(accessToken, SmallBody(UniqueRegistrationNumber()));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CreateBus_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync(Route, SmallBody(UniqueRegistrationNumber()));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    // Letters A-F and digits: a fresh, valid registration number, so tests sharing the database never collide.
    private static string UniqueRegistrationNumber()
    {
        return "TS" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
    }

    // The bus of the "add a two-deck sleeper" criterion: two 6x3 decks, aisle after column 1, 36 berths.
    private static JsonObject SleeperBody(string registrationNumber)
    {
        var seats = new JsonArray();
        AddGridSeats(seats, "lower", "L", 6, 3, "Lower berth");
        AddGridSeats(seats, "upper", "U", 6, 3, "Upper berth");
        return Body(registrationNumber, Deck("lower", 6, 3, 1), Deck("upper", 6, 3, 1), seats);
    }

    // A minimal valid two-deck bus: two 2x2 decks, eight seats, both classes used.
    private static JsonObject SmallBody(string registrationNumber)
    {
        var seats = new JsonArray();
        AddGridSeats(seats, "lower", "L", 2, 2, "Lower berth");
        AddGridSeats(seats, "upper", "U", 2, 2, "Upper berth");
        return Body(registrationNumber, Deck("lower", 2, 2, 1), Deck("upper", 2, 2, 1), seats);
    }

    private static JsonObject InvalidLayoutBody(string scenario, string registrationNumber)
    {
        JsonObject body = SmallBody(registrationNumber);
        JsonArray seats = body["layout"]!["seats"]!.AsArray();
        JsonArray decks = body["layout"]!["decks"]!.AsArray();
        switch (scenario)
        {
            case "label used twice ignoring case":
                seats[0]!["label"] = "1A";
                seats[1]!["label"] = "1a";
                break;
            case "seat outside the deck's rows":
                decks[0]!["rows"] = 6;
                seats[0]!["row"] = 7;
                break;
            case "two seats in one place":
                seats[1]!["row"] = seats[0]!["row"]!.GetValue<int>();
                seats[1]!["column"] = seats[0]!["column"]!.GetValue<int>();
                break;
            case "seat class the bus lacks":
                seats[0]!["seatClass"] = "Window";
                break;
            case "seat class with no seat":
                body["layout"]!["seats"] = LowerSeatsOnly();
                break;
            case "61 seats":
                body["layout"]!["seats"] = ManySeats(61);
                break;
            case "no lower deck":
                decks[0]!["deck"] = "upper";
                break;
            case "aisle after the last column":
                decks[0]!["columns"] = 3;
                decks[0]!["aisleAfterColumn"] = 3;
                break;
            default:
                throw new ArgumentException($"Unknown scenario '{scenario}'.", nameof(scenario));
        }

        return body;
    }

    private static JsonObject InvalidFieldBody(string scenario)
    {
        JsonObject body = SmallBody(UniqueRegistrationNumber());
        switch (scenario)
        {
            case "registration number of 5 characters":
                body["registrationNumber"] = "MH-12A";
                break;
            case "name of 1 character":
                body["name"] = "A";
                break;
            case "6 seat classes":
                body["seatClasses"] = new JsonArray("Lower berth", "Upper berth", "Window", "Aisle", "Middle", "Sixth");
                break;
            case "seat classes differing only in case":
                body["seatClasses"] = new JsonArray("Lower berth", "lower berth");
                break;
            case "amenity listed twice":
                body["amenities"] = new JsonArray("wifi", "wifi");
                break;
            case "amenity not in the list":
                body["amenities"] = new JsonArray("jacuzzi");
                break;
            default:
                throw new ArgumentException($"Unknown scenario '{scenario}'.", nameof(scenario));
        }

        return body;
    }

    private static JsonObject Body(string registrationNumber, JsonObject lowerDeck, JsonObject upperDeck, JsonArray seats)
    {
        return new JsonObject
        {
            ["registrationNumber"] = registrationNumber,
            ["name"] = "Shivneri Sleeper",
            ["busType"] = "sleeper",
            ["isAc"] = true,
            ["amenities"] = new JsonArray("blanket", "chargingPoint"),
            ["seatClasses"] = new JsonArray("Lower berth", "Upper berth"),
            ["layout"] = new JsonObject
            {
                ["decks"] = new JsonArray(lowerDeck, upperDeck),
                ["seats"] = seats,
            },
        };
    }

    private static JsonObject Deck(string deck, int rows, int columns, int? aisleAfterColumn)
    {
        return new JsonObject
        {
            ["deck"] = deck,
            ["rows"] = rows,
            ["columns"] = columns,
            ["aisleAfterColumn"] = aisleAfterColumn,
        };
    }

    private static void AddGridSeats(JsonArray seats, string deck, string prefix, int rows, int columns, string seatClass)
    {
        for (int row = 1; row <= rows; row++)
        {
            for (int column = 1; column <= columns; column++)
            {
                seats.Add(Seat($"{prefix}{seats.Count + 1}", deck, row, column, seatClass));
            }
        }
    }

    private static JsonArray LowerSeatsOnly()
    {
        var seats = new JsonArray();
        AddGridSeats(seats, "lower", "L", 2, 2, "Lower berth");
        return seats;
    }

    private static JsonArray ManySeats(int count)
    {
        var seats = new JsonArray();
        for (int index = 1; index <= count; index++)
        {
            seats.Add(Seat($"S{index}", "lower", 1, 1, "Lower berth"));
        }

        return seats;
    }

    private static JsonObject Seat(string label, string deck, int row, int column, string seatClass)
    {
        return new JsonObject
        {
            ["label"] = label,
            ["deck"] = deck,
            ["row"] = row,
            ["column"] = column,
            ["seatClass"] = seatClass,
        };
    }

    private static async Task AssertValidationFailedAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("urn:routego:fleet:problems:validation-failed", problem.RootElement.GetProperty("type").GetString());
        IEnumerable<string> fields = problem.RootElement.GetProperty("errors").EnumerateObject().Select(error => error.Name);
        Assert.Contains(fields, name => name.Contains(field, StringComparison.Ordinal));
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<HttpResponseMessage> CreateBusAsync(string accessToken, JsonObject body)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, Route, accessToken, body);
        return await client.SendAsync(request);
    }
}
