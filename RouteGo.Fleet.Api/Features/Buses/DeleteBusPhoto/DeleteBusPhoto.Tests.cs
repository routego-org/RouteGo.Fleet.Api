using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Buses.DeleteBusPhoto;

[Collection(DatabaseCollection.Name)]
public sealed class DeleteBusPhotoTests
{
    private static readonly byte[] _jpegFile = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };

    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Seater', true, NULL, 'Active');
        """;

    private const string InsertPhotoSql = """
        INSERT INTO buses.bus_photos (photo_id, bus_id, file_location, format, size_bytes, status, position, slot_expires_at, uploaded_at)
        VALUES (@PhotoId, @BusId, @FileLocation, 'Jpeg', 1000, @Status, @Position, now() + interval '5 minutes',
                CASE WHEN @Status = 'Uploaded' THEN now() END);
        """;

    private const string SelectOrderSql = """
        SELECT coalesce(string_agg(photo_id::text, ',' ORDER BY position), '')
        FROM buses.bus_photos
        WHERE bus_id = @BusId AND status = 'Uploaded'
        """;

    private const string CountPhotosSql = "SELECT count(*) FROM buses.bus_photos WHERE photo_id = @PhotoId";

    private readonly DatabaseFixture _database;

    public DeleteBusPhotoTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task DeleteBusPhoto_MiddleOfThreePhotos_RemovesItAndItsFileAndClosesTheGap()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);
        (Guid first, string _) = await SeedPhotoAsync(busId, "Uploaded", 1);
        (Guid second, string secondLocation) = await SeedPhotoAsync(busId, "Uploaded", 2);
        (Guid third, string _) = await SeedPhotoAsync(busId, "Uploaded", 3);

        // Act
        using HttpResponseMessage response = await DeleteBusPhotoAsync(TestAccessTokens.CreateOperator(operatorId), busId, second);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await _database.QueryScalarAsync<long>(CountPhotosSql, new { PhotoId = second }));
        Assert.False(await TestMediaStore.ExistsAsync(secondLocation));
        Assert.Equal($"{first},{third}", await _database.QueryScalarAsync<string>(SelectOrderSql, new { BusId = busId }));
    }

    [Fact]
    public async Task DeleteBusPhoto_PendingSlot_RemovesItAndLeavesTheOrderAlone()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);
        (Guid first, string _) = await SeedPhotoAsync(busId, "Uploaded", 1);
        (Guid pending, string _) = await SeedPhotoAsync(busId, "Pending", null);

        // Act
        using HttpResponseMessage response = await DeleteBusPhotoAsync(TestAccessTokens.CreateOperator(operatorId), busId, pending);

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0L, await _database.QueryScalarAsync<long>(CountPhotosSql, new { PhotoId = pending }));
        Assert.Equal(first.ToString(), await _database.QueryScalarAsync<string>(SelectOrderSql, new { BusId = busId }));
    }

    [Fact]
    public async Task DeleteBusPhoto_UnknownPhoto_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);

        // Act
        using HttpResponseMessage response = await DeleteBusPhotoAsync(TestAccessTokens.CreateOperator(operatorId), busId, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task DeleteBusPhoto_PhotoOfAnotherOperatorsBus_IsReportedNotFoundAndKept()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(owner);
        (Guid photoId, string fileLocation) = await SeedPhotoAsync(busId, "Uploaded", 1);

        // Act
        using HttpResponseMessage response = await DeleteBusPhotoAsync(TestAccessTokens.CreateOperator(otherOperator), busId, photoId);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal(1L, await _database.QueryScalarAsync<long>(CountPhotosSql, new { PhotoId = photoId }));
        Assert.True(await TestMediaStore.ExistsAsync(fileLocation));
    }

    [Fact]
    public async Task DeleteBusPhoto_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await DeleteBusPhotoAsync(accessToken, Guid.NewGuid(), Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task DeleteBusPhoto_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await DeleteBusPhotoAsync(accessToken, Guid.NewGuid(), Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task DeleteBusPhoto_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database, await TestMediaStore.OverrideAsync());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.DeleteAsync($"/buses/{Guid.NewGuid()}/photos/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Guid> SeedBusAsync(Guid operatorId)
    {
        var busId = Guid.NewGuid();
        await _database.SeedAsync(InsertBusSql, new
        {
            BusId = busId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "TD" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
        });
        return busId;
    }

    // Every seeded photo has its file in the media store, as an uploaded or in-flight one would.
    private async Task<(Guid PhotoId, string FileLocation)> SeedPhotoAsync(Guid busId, string status, int? position)
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
        await TestMediaStore.PutFileAsync(fileLocation, "image/jpeg", _jpegFile);
        return (photoId, fileLocation);
    }

    private async Task<HttpResponseMessage> DeleteBusPhotoAsync(string accessToken, Guid busId, Guid photoId)
    {
        using ApiFactory factory = FleetTestHost.For(_database, await TestMediaStore.OverrideAsync());
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Delete, $"/buses/{busId}/photos/{photoId}", accessToken);
        return await client.SendAsync(request);
    }
}
