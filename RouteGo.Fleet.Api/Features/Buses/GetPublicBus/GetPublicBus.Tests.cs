using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Buses.GetPublicBus;

[Collection(DatabaseCollection.Name)]
public sealed class GetPublicBusTests
{
    // The local CDN base address every test host is configured with.
    private const string CdnBaseUrl = "http://localhost:5002/media/";

    // A sleeper with two seat classes and four seats; TestOperators seeds "BusCo Travels", rating 4.0.
    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Sleeper', true, ARRAY['Blanket', 'ChargingPoint'], @Status);
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 2, 2, 1);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Lower berth', 1), (@BusId, 'Upper berth', 2);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'L1', 'Lower', 1, 1, 'Lower berth'), (@BusId, 'L2', 'Lower', 1, 2, 'Lower berth'),
               (@BusId, 'U1', 'Lower', 2, 1, 'Upper berth'), (@BusId, 'U2', 'Lower', 2, 2, 'Upper berth');
        """;

    private const string InsertPhotoSql = """
        INSERT INTO buses.bus_photos (photo_id, bus_id, file_location, format, size_bytes, status, position, slot_expires_at, uploaded_at)
        VALUES (@PhotoId, @BusId, @FileLocation, 'Jpeg', 1000, @Status, @Position, now() + interval '5 minutes',
                CASE WHEN @Status = 'Uploaded' THEN now() END);
        """;

    private readonly DatabaseFixture _database;

    public GetPublicBusTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task GetPublicBus_WithoutAccessToken_ReturnsOnlyThePublicDetailsAndPhotosInOrder()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId, "Active");
        string cover = await SeedPhotoAsync(busId, "Uploaded", 1);
        string second = await SeedPhotoAsync(busId, "Uploaded", 2);
        await SeedPhotoAsync(busId, "Pending", null);

        // Act
        using HttpResponseMessage response = await GetPublicBusAsync(busId, null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement bus = document.RootElement;
        Assert.Equal("Alpha", bus.GetProperty("name").GetString());
        Assert.Equal("sleeper", bus.GetProperty("busType").GetString());
        Assert.True(bus.GetProperty("isAc").GetBoolean());
        Assert.Equal(new[] { "blanket", "chargingPoint" }, Strings(bus.GetProperty("amenities")));
        Assert.Equal(new[] { "Lower berth", "Upper berth" }, Strings(bus.GetProperty("seatClasses")));
        Assert.Equal(4, bus.GetProperty("totalSeats").GetInt32());
        Assert.Equal("BusCo Travels", bus.GetProperty("operatorName").GetString());
        Assert.Equal(4.0m, bus.GetProperty("operatorRating").GetDecimal());
        Assert.Equal(new[] { CdnBaseUrl + cover, CdnBaseUrl + second }, Strings(bus.GetProperty("photos"), "url"));
        Assert.Equal(
            new[] { "amenities", "busId", "busType", "isAc", "name", "operatorName", "operatorRating", "photos", "seatClasses", "totalSeats" },
            PropertyNames(bus));
    }

    [Fact]
    public async Task GetPublicBus_InactiveBus_StillShows()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId, "Inactive");

        // Act
        using HttpResponseMessage response = await GetPublicBusAsync(busId, null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Alpha", document.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetPublicBus_NoUploadedPhoto_ReturnsAnEmptyPhotoList()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId, "Active");

        // Act
        using HttpResponseMessage response = await GetPublicBusAsync(busId, null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, document.RootElement.GetProperty("photos").GetArrayLength());
    }

    [Fact]
    public async Task GetPublicBus_UnknownBus_IsRefusedNotFound()
    {
        // Act
        using HttpResponseMessage response = await GetPublicBusAsync(Guid.NewGuid(), null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("NOT_FOUND", problem.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Operator")]
    [InlineData("Admin")]
    public async Task GetPublicBus_WithAnyAccessToken_ReturnsTheSameAsWithout(string role)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId, "Active");
        await SeedPhotoAsync(busId, "Uploaded", 1);
        using HttpResponseMessage anonymous = await GetPublicBusAsync(busId, null);

        // Act
        using HttpResponseMessage response = await GetPublicBusAsync(busId, TestAccessTokens.Create(role));

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(await anonymous.Content.ReadAsStringAsync(), await response.Content.ReadAsStringAsync());
    }

    private static string?[] Strings(JsonElement array)
    {
        JsonElement.ArrayEnumerator items = array.EnumerateArray();
        return items.Select(item => item.GetString()).ToArray();
    }

    private static string?[] Strings(JsonElement array, string property)
    {
        JsonElement.ArrayEnumerator items = array.EnumerateArray();
        return items.Select(item => item.GetProperty(property).GetString()).ToArray();
    }

    private static string[] PropertyNames(JsonElement element)
    {
        JsonElement.ObjectEnumerator properties = element.EnumerateObject();
        return properties.Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
    }

    private async Task<Guid> SeedBusAsync(Guid operatorId, string status)
    {
        var busId = Guid.NewGuid();
        await _database.SeedAsync(InsertBusSql, new
        {
            BusId = busId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "TG" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            Status = status,
        });
        return busId;
    }

    private async Task<string> SeedPhotoAsync(Guid busId, string status, int? position)
    {
        var photoId = Guid.NewGuid();
        string fileLocation = $"buses/{busId}/photos/{photoId}";
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

    private async Task<HttpResponseMessage> GetPublicBusAsync(Guid busId, string? accessToken)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/public/buses/{busId}");
        if (accessToken != null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await client.SendAsync(request);
    }
}
