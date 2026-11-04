using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Buses.GetBus;

[Collection(DatabaseCollection.Name)]
public sealed class GetBusTests
{
    // The local CDN base address every test host is configured with.
    private const string CdnBaseUrl = "http://localhost:5002/media/";

    // A two-deck sleeper: three lower berths below the aisle-split lower deck, two upper berths above.
    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Sleeper', true, ARRAY['Blanket', 'ChargingPoint'], 'Active');
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 6, 3, 1), (@BusId, 'Upper', 6, 2, NULL);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Lower berth', 1), (@BusId, 'Upper berth', 2);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'L1', 'Lower', 1, 1, 'Lower berth'), (@BusId, 'L2', 'Lower', 1, 2, 'Lower berth'),
               (@BusId, 'L3', 'Lower', 1, 3, 'Lower berth'), (@BusId, 'U1', 'Upper', 1, 1, 'Upper berth'),
               (@BusId, 'U2', 'Upper', 1, 2, 'Upper berth');
        """;

    private const string InsertPhotoSql = """
        INSERT INTO buses.bus_photos (photo_id, bus_id, file_location, format, size_bytes, status, position, slot_expires_at, uploaded_at)
        VALUES (
            @PhotoId, @BusId, @FileLocation, 'Jpeg', 2048, @Status, @Position, now() + interval '5 minutes',
            CASE WHEN @Status = 'Uploaded' THEN now() END);
        """;

    private readonly DatabaseFixture _database;

    public GetBusTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task GetBus_OwnBusWithTwoUploadedPhotos_ReturnsEveryDetailAndThePhotosInOrder()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);
        await SeedPhotoAsync(busId, "Pending", null);
        string secondLocation = await SeedPhotoAsync(busId, "Uploaded", 2);
        string coverLocation = await SeedPhotoAsync(busId, "Uploaded", 1);

        // Act
        using HttpResponseMessage response = await GetBusAsync(TestAccessTokens.CreateOperator(operatorId), busId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement bus = document.RootElement;
        Assert.Equal("Alpha", bus.GetProperty("name").GetString());
        Assert.Equal("sleeper", bus.GetProperty("busType").GetString());
        Assert.Equal("active", bus.GetProperty("status").GetString());
        Assert.Equal(new[] { "blanket", "chargingPoint" }, Strings(bus.GetProperty("amenities")));
        Assert.Equal(new[] { "Lower berth", "Upper berth" }, Strings(bus.GetProperty("seatClasses")));
        JsonElement layout = bus.GetProperty("layout");
        Assert.Equal(new[] { "lower", "upper" }, Values(layout.GetProperty("decks"), "deck"));
        Assert.Equal(JsonValueKind.Null, layout.GetProperty("decks")[1].GetProperty("aisleAfterColumn").ValueKind);
        Assert.Equal(new[] { "L1", "L2", "L3", "U1", "U2" }, Values(layout.GetProperty("seats"), "label"));
        Assert.Equal(3, layout.GetProperty("seats")[2].GetProperty("column").GetInt32());
        Assert.Equal(5, bus.GetProperty("totalSeats").GetInt32());
        JsonElement photos = bus.GetProperty("photos");
        Assert.Equal(new[] { CdnBaseUrl + coverLocation, CdnBaseUrl + secondLocation }, Values(photos, "url"));
        Assert.Equal(new[] { "uploaded", "uploaded" }, Values(photos, "status"));
    }

    [Fact]
    public async Task GetBus_UnknownBus_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await GetBusAsync(TestAccessTokens.CreateOperator(operatorId), Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetBus_AnotherOperatorsBus_IsReportedNotFound()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(owner);

        // Act
        using HttpResponseMessage response = await GetBusAsync(TestAccessTokens.CreateOperator(otherOperator), busId);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetBus_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await GetBusAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task GetBus_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await GetBusAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task GetBus_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync($"/buses/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static string?[] Strings(JsonElement array)
    {
        JsonElement.ArrayEnumerator items = array.EnumerateArray();
        return items.Select(item => item.GetString()).ToArray();
    }

    private static string?[] Values(JsonElement array, string property)
    {
        JsonElement.ArrayEnumerator items = array.EnumerateArray();
        return items.Select(item => item.GetProperty(property).GetString()).ToArray();
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    // Registration numbers are unique across every operator, so each bus gets a fresh one.
    private async Task<Guid> SeedBusAsync(Guid operatorId)
    {
        var busId = Guid.NewGuid();
        await _database.SeedAsync(InsertBusSql, new
        {
            BusId = busId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "T" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant(),
        });
        return busId;
    }

    private async Task<string> SeedPhotoAsync(Guid busId, string status, int? position)
    {
        var photoId = Guid.NewGuid();
        string fileLocation = $"buses/{busId}/{photoId}.jpg";
        await _database.SeedAsync(InsertPhotoSql, new
        {
            PhotoId = photoId,
            BusId = busId,
            FileLocation = fileLocation,
            Status = status,
            Position = position,
        });
        return fileLocation;
    }

    private async Task<HttpResponseMessage> GetBusAsync(string accessToken, Guid busId)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, $"/buses/{busId}", accessToken);
        return await client.SendAsync(request);
    }
}
