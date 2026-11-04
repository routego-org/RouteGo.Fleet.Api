using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouteGo.Fleet.Api.Features.Buses.ReplaceBusPhotoOrder;

[Collection(DatabaseCollection.Name)]
public sealed class ReplaceBusPhotoOrderTests
{
    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Seater', true, NULL, 'Active');
        """;

    private const string InsertPhotoSql = """
        INSERT INTO buses.bus_photos (photo_id, bus_id, file_location, format, size_bytes, status, position, slot_expires_at, uploaded_at)
        VALUES (@PhotoId, @BusId, 'buses/seeded/' || @PhotoId, 'Jpeg', 1000, 'Uploaded', @Position, now(), now());
        """;

    private const string SelectOrderSql = """
        SELECT string_agg(photo_id::text, ',' ORDER BY position)
        FROM buses.bus_photos
        WHERE bus_id = @BusId AND status = 'Uploaded'
        """;

    private readonly DatabaseFixture _database;

    public ReplaceBusPhotoOrderTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ReplaceBusPhotoOrder_ThirdPhotoFirst_ReturnsTheNewOrderWithItAsCover()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        (Guid busId, Guid[] photos) = await SeedBusWithThreePhotosAsync(operatorId);
        JsonObject body = Body(photos[2], photos[0], photos[1]);

        // Act
        using HttpResponseMessage response = await ReplaceBusPhotoOrderAsync(TestAccessTokens.CreateOperator(operatorId), busId, body);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var list = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement items = list.RootElement.GetProperty("items");
        Assert.Equal(photos[2], items[0].GetProperty("photoId").GetGuid());
        Assert.Equal(1, items[0].GetProperty("position").GetInt32());
        Assert.Equal(photos[0], items[1].GetProperty("photoId").GetGuid());
        Assert.Equal(photos[1], items[2].GetProperty("photoId").GetGuid());
        Assert.Equal($"{photos[2]},{photos[0]},{photos[1]}", await StoredOrderAsync(busId));
    }

    [Theory]
    [InlineData("a photo left out")]
    [InlineData("a photo listed twice")]
    [InlineData("a photo the bus lacks")]
    public async Task ReplaceBusPhotoOrder_ListIsNotExactlyTheUploadedPhotos_IsRefusedAndOrderUnchanged(string mistake)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        (Guid busId, Guid[] photos) = await SeedBusWithThreePhotosAsync(operatorId);
        JsonObject body = WrongBody(mistake, photos);

        // Act
        using HttpResponseMessage response = await ReplaceBusPhotoOrderAsync(TestAccessTokens.CreateOperator(operatorId), busId, body);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("photoIds", out JsonElement _));
        Assert.Equal($"{photos[0]},{photos[1]},{photos[2]}", await StoredOrderAsync(busId));
    }

    [Fact]
    public async Task ReplaceBusPhotoOrder_UnknownBus_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await ReplaceBusPhotoOrderAsync(
            TestAccessTokens.CreateOperator(operatorId), Guid.NewGuid(), Body(Guid.NewGuid()));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ReplaceBusPhotoOrder_AnotherOperatorsBus_IsReportedNotFoundAndOrderUnchanged()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        (Guid busId, Guid[] photos) = await SeedBusWithThreePhotosAsync(owner);

        // Act
        using HttpResponseMessage response = await ReplaceBusPhotoOrderAsync(
            TestAccessTokens.CreateOperator(otherOperator), busId, Body(photos[2], photos[1], photos[0]));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal($"{photos[0]},{photos[1]},{photos[2]}", await StoredOrderAsync(busId));
    }

    [Fact]
    public async Task ReplaceBusPhotoOrder_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await ReplaceBusPhotoOrderAsync(accessToken, Guid.NewGuid(), Body(Guid.NewGuid()));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task ReplaceBusPhotoOrder_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await ReplaceBusPhotoOrderAsync(accessToken, Guid.NewGuid(), Body(Guid.NewGuid()));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ReplaceBusPhotoOrder_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent(Body(Guid.NewGuid()).ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PutAsync($"/buses/{Guid.NewGuid()}/photoOrder", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static JsonObject Body(params Guid[] photoIds)
    {
        var list = new JsonArray();
        foreach (Guid photoId in photoIds)
        {
            list.Add(photoId);
        }

        return new JsonObject { ["photoIds"] = list };
    }

    private static JsonObject WrongBody(string mistake, Guid[] photos)
    {
        switch (mistake)
        {
            case "a photo left out":
                return Body(photos[0], photos[1]);
            case "a photo listed twice":
                return Body(photos[0], photos[1], photos[2], photos[2]);
            case "a photo the bus lacks":
                return Body(photos[0], photos[1], Guid.NewGuid());
            default:
                throw new ArgumentException($"Unknown mistake '{mistake}'.", nameof(mistake));
        }
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<(Guid BusId, Guid[] Photos)> SeedBusWithThreePhotosAsync(Guid operatorId)
    {
        var busId = Guid.NewGuid();
        await _database.SeedAsync(InsertBusSql, new
        {
            BusId = busId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "TO" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
        });

        Guid[] photos = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        for (int index = 0; index < photos.Length; index++)
        {
            await _database.SeedAsync(InsertPhotoSql, new { PhotoId = photos[index], BusId = busId, Position = index + 1 });
        }

        return (busId, photos);
    }

    private async Task<string?> StoredOrderAsync(Guid busId)
    {
        return await _database.QueryScalarAsync<string>(SelectOrderSql, new { BusId = busId });
    }

    private async Task<HttpResponseMessage> ReplaceBusPhotoOrderAsync(string accessToken, Guid busId, JsonObject body)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Put, $"/buses/{busId}/photoOrder", accessToken, body);
        return await client.SendAsync(request);
    }
}
