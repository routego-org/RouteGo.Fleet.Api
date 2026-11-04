using System.Text.Json;
using System.Text.Json.Nodes;
using RouteGo.Fleet.Api.Infrastructure;

namespace RouteGo.Fleet.Api.Features.Buses.CreateBusPhoto;

[Collection(DatabaseCollection.Name)]
public sealed class CreateBusPhotoTests
{
    // The start of a JPEG file: enough for the media store to accept an upload.
    private static readonly byte[] _jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };

    // The real time, to the second: the media store signs the upload address against its own clock,
    // so the host's clock is pinned to now rather than to a fixed date.
    private static readonly DateTimeOffset _now = ToWholeSeconds(new SystemClock().UtcNow);

    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Seater', true, NULL, 'Active');
        """;

    private const string InsertPhotoSql = """
        INSERT INTO buses.bus_photos (photo_id, bus_id, file_location, format, size_bytes, status, position, slot_expires_at, uploaded_at)
        VALUES (@PhotoId, @BusId, 'buses/seeded/' || @PhotoId, 'Jpeg', 1000, @Status, @Position, @SlotExpiresAt,
                CASE WHEN @Status = 'Uploaded' THEN @SlotExpiresAt END);
        """;

    private const string SelectPhotoSql = """
        SELECT status || ' ' || format || ' ' || size_bytes || ' ' || file_location
        FROM buses.bus_photos
        WHERE photo_id = @PhotoId
        """;

    private readonly DatabaseFixture _database;

    public CreateBusPhotoTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task CreateBusPhoto_BusWithTwoPhotos_IssuesASlotTheBrowserCanUploadTo()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);
        await SeedPhotosAsync(busId, "Uploaded", 2, _now.AddDays(-1));

        // Act
        using HttpResponseMessage response = await CreateBusPhotoAsync(
            TestAccessTokens.CreateOperator(operatorId), busId, Body("jpeg", 1_200_000));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var slot = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid photoId = slot.RootElement.GetProperty("photoId").GetGuid();
        string fileLocation = $"buses/{busId}/photos/{photoId}";
        Assert.Equal("put", slot.RootElement.GetProperty("uploadMethod").GetString());
        Assert.Equal("image/jpeg", slot.RootElement.GetProperty("contentType").GetString());
        Assert.Equal(_now.AddMinutes(5), slot.RootElement.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal($"Pending Jpeg 1200000 {fileLocation}", await StoredPhotoAsync(photoId));
        string uploadUrl = slot.RootElement.GetProperty("uploadUrl").GetString()!;
        Assert.Equal(HttpStatusCode.OK, await TestMediaStore.UploadAsync(uploadUrl, "image/jpeg", _jpegBytes));
        Assert.True(await TestMediaStore.ExistsAsync(fileLocation));
    }

    [Fact]
    public async Task CreateBusPhoto_FourPhotosAndAnOpenSlot_IsRefusedPhotoLimitReached()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);
        await SeedPhotosAsync(busId, "Uploaded", 4, _now.AddDays(-1));
        await SeedPhotosAsync(busId, "Pending", 1, _now.AddMinutes(2));

        // Act
        using HttpResponseMessage response = await CreateBusPhotoAsync(
            TestAccessTokens.CreateOperator(operatorId), busId, Body("png", 2048));

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PHOTO_LIMIT_REACHED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(5, problem.RootElement.GetProperty("maxPhotos").GetInt32());
    }

    [Fact]
    public async Task CreateBusPhoto_FourPhotosAndAnExpiredSlot_IssuesASlot()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);
        await SeedPhotosAsync(busId, "Uploaded", 4, _now.AddDays(-1));
        await SeedPhotosAsync(busId, "Pending", 1, _now.AddMinutes(-1));

        // Act
        using HttpResponseMessage response = await CreateBusPhotoAsync(
            TestAccessTokens.CreateOperator(operatorId), busId, Body("png", 2048));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var slot = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("image/png", slot.RootElement.GetProperty("contentType").GetString());
    }

    [Theory]
    [InlineData("gif", 2048L, "format")]
    [InlineData("jpeg", 3_145_729L, "sizeBytes")]
    [InlineData("jpeg", 0L, "sizeBytes")]
    public async Task CreateBusPhoto_FormatOrSizeBreaksARule_IsRefusedNamingTheField(string format, long sizeBytes, string field)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);

        // Act
        using HttpResponseMessage response = await CreateBusPhotoAsync(
            TestAccessTokens.CreateOperator(operatorId), busId, Body(format, sizeBytes));

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        IEnumerable<string> fields = problem.RootElement.GetProperty("errors").EnumerateObject().Select(error => error.Name);
        Assert.Contains(fields, name => name.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateBusPhoto_UnknownBus_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await CreateBusPhotoAsync(
            TestAccessTokens.CreateOperator(operatorId), Guid.NewGuid(), Body("jpeg", 2048));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CreateBusPhoto_AnotherOperatorsBus_IsReportedNotFound()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(owner);

        // Act
        using HttpResponseMessage response = await CreateBusPhotoAsync(
            TestAccessTokens.CreateOperator(otherOperator), busId, Body("jpeg", 2048));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CreateBusPhoto_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await CreateBusPhotoAsync(accessToken, Guid.NewGuid(), Body("jpeg", 2048));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task CreateBusPhoto_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await CreateBusPhotoAsync(accessToken, Guid.NewGuid(), Body("jpeg", 2048));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CreateBusPhoto_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database, await TestMediaStore.OverrideAsync());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/buses/{Guid.NewGuid()}/photos", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static DateTimeOffset ToWholeSeconds(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    private static JsonObject Body(string format, long sizeBytes)
    {
        return new JsonObject
        {
            ["format"] = format,
            ["sizeBytes"] = sizeBytes,
        };
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
            RegistrationNumber = "TP" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
        });
        return busId;
    }

    // Uploaded photos take positions 1, 2, 3 ... in seeding order; pending slots have none.
    private async Task SeedPhotosAsync(Guid busId, string status, int count, DateTimeOffset slotExpiresAt)
    {
        int existing = await _database.QueryScalarAsync<int>(
            "SELECT count(*)::int FROM buses.bus_photos WHERE bus_id = @BusId AND status = 'Uploaded'", new { BusId = busId });
        for (int index = 1; index <= count; index++)
        {
            int? position = null;
            if (status == "Uploaded")
            {
                position = existing + index;
            }

            await _database.SeedAsync(InsertPhotoSql, new
            {
                PhotoId = Guid.NewGuid(),
                BusId = busId,
                Status = status,
                Position = position,
                SlotExpiresAt = slotExpiresAt,
            });
        }
    }

    private async Task<string?> StoredPhotoAsync(Guid photoId)
    {
        return await _database.QueryScalarAsync<string>(SelectPhotoSql, new { PhotoId = photoId });
    }

    private async Task<HttpResponseMessage> CreateBusPhotoAsync(string accessToken, Guid busId, JsonObject body)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(_now), await TestMediaStore.OverrideAsync());
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, $"/buses/{busId}/photos", accessToken, body);
        return await client.SendAsync(request);
    }
}
