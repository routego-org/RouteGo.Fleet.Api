using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Buses.ConfirmBusPhoto;

[Collection(DatabaseCollection.Name)]
public sealed class ConfirmBusPhotoTests
{
    // The local CDN base address every test host is configured with.
    private const string CdnBaseUrl = "http://localhost:5002/media/";

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 10, 8, 30, 0, TimeSpan.Zero);

    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Seater', true, NULL, 'Active');
        """;

    private const string InsertPhotoSql = """
        INSERT INTO buses.bus_photos (photo_id, bus_id, file_location, format, size_bytes, status, position, slot_expires_at, uploaded_at)
        VALUES (@PhotoId, @BusId, @FileLocation, @Format, 1000, @Status, @Position, @SlotExpiresAt,
                CASE WHEN @Status = 'Uploaded' THEN @SlotExpiresAt END);
        """;

    private const string SelectPhotoSql = """
        SELECT status || ' ' || size_bytes || ' ' || coalesce(position::text, '-')
        FROM buses.bus_photos
        WHERE photo_id = @PhotoId
        """;

    private readonly DatabaseFixture _database;

    public ConfirmBusPhotoTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ConfirmBusPhoto_GoodJpegOnBusWithTwoPhotos_BecomesThirdUploadedPhoto()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);
        await SeedPhotoAsync(busId, "Jpeg", "Uploaded", 1, _now.AddDays(-1));
        await SeedPhotoAsync(busId, "Jpeg", "Uploaded", 2, _now.AddDays(-1));
        (Guid photoId, string fileLocation) = await SeedPhotoAsync(busId, "Jpeg", "Pending", null, _now.AddMinutes(3));
        await TestMediaStore.PutFileAsync(fileLocation, "image/jpeg", FileOf(_jpegSignature, 1_150_000));

        // Act
        using HttpResponseMessage response = await ConfirmBusPhotoAsync(TestAccessTokens.CreateOperator(operatorId), busId, photoId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var photo = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("uploaded", photo.RootElement.GetProperty("status").GetString());
        Assert.Equal(1_150_000, photo.RootElement.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(3, photo.RootElement.GetProperty("position").GetInt32());
        Assert.Equal(CdnBaseUrl + fileLocation, photo.RootElement.GetProperty("url").GetString());
        Assert.Equal(_now, photo.RootElement.GetProperty("uploadedAt").GetDateTimeOffset());
        Assert.Equal("Uploaded 1150000 3", await StoredPhotoAsync(photoId));
    }

    [Fact]
    public async Task ConfirmBusPhoto_SlotExpiredAMinuteAgo_IsRefusedAndSlotAndFileDiscarded()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);
        DateTimeOffset expiredAt = _now.AddMinutes(-1);
        (Guid photoId, string fileLocation) = await SeedPhotoAsync(busId, "Jpeg", "Pending", null, expiredAt);
        await TestMediaStore.PutFileAsync(fileLocation, "image/jpeg", FileOf(_jpegSignature, 2048));

        // Act
        using HttpResponseMessage response = await ConfirmBusPhotoAsync(TestAccessTokens.CreateOperator(operatorId), busId, photoId);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PHOTO_UPLOAD_EXPIRED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(expiredAt, problem.RootElement.GetProperty("expiredAt").GetDateTimeOffset());
        Assert.Null(await StoredPhotoAsync(photoId));
        Assert.False(await TestMediaStore.ExistsAsync(fileLocation));
    }

    [Fact]
    public async Task ConfirmBusPhoto_FileNotSentYet_IsRefusedAndSlotStaysPending()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);
        (Guid photoId, string _) = await SeedPhotoAsync(busId, "Jpeg", "Pending", null, _now.AddMinutes(3));

        // Act
        using HttpResponseMessage response = await ConfirmBusPhotoAsync(TestAccessTokens.CreateOperator(operatorId), busId, photoId);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PHOTO_NOT_UPLOADED", await ProblemCodeAsync(response));
        Assert.Equal("Pending 1000 -", await StoredPhotoAsync(photoId));
    }

    [Theory]
    [InlineData("4 MB file", "tooLarge")]
    [InlineData("PNG file", "formatMismatch")]
    [InlineData("GIF file", "unsupportedFormat")]
    public async Task ConfirmBusPhoto_StoredFileBreaksTheRules_IsRefusedAndSlotAndFileDiscarded(string file, string reason)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);
        (Guid photoId, string fileLocation) = await SeedPhotoAsync(busId, "Jpeg", "Pending", null, _now.AddMinutes(3));
        await TestMediaStore.PutFileAsync(fileLocation, "image/jpeg", BadFile(file));

        // Act
        using HttpResponseMessage response = await ConfirmBusPhotoAsync(TestAccessTokens.CreateOperator(operatorId), busId, photoId);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PHOTO_INVALID", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(reason, problem.RootElement.GetProperty("reason").GetString());
        Assert.Null(await StoredPhotoAsync(photoId));
        Assert.False(await TestMediaStore.ExistsAsync(fileLocation));
    }

    [Fact]
    public async Task ConfirmBusPhoto_AlreadyUploaded_ReturnsPhotoUnchanged()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);
        await SeedPhotoAsync(busId, "Png", "Uploaded", 1, _now.AddDays(-1));
        (Guid photoId, string _) = await SeedPhotoAsync(busId, "Png", "Uploaded", 2, _now.AddDays(-1));

        // Act
        using HttpResponseMessage response = await ConfirmBusPhotoAsync(TestAccessTokens.CreateOperator(operatorId), busId, photoId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var photo = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(2, photo.RootElement.GetProperty("position").GetInt32());
        Assert.Equal("Uploaded 1000 2", await StoredPhotoAsync(photoId));
    }

    [Fact]
    public async Task ConfirmBusPhoto_UnknownPhoto_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId);

        // Act
        using HttpResponseMessage response = await ConfirmBusPhotoAsync(TestAccessTokens.CreateOperator(operatorId), busId, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ConfirmBusPhoto_PhotoOfAnotherOperatorsBus_IsReportedNotFound()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(owner);
        (Guid photoId, string _) = await SeedPhotoAsync(busId, "Jpeg", "Pending", null, _now.AddMinutes(3));

        // Act
        using HttpResponseMessage response = await ConfirmBusPhotoAsync(TestAccessTokens.CreateOperator(otherOperator), busId, photoId);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal("Pending 1000 -", await StoredPhotoAsync(photoId));
    }

    [Fact]
    public async Task ConfirmBusPhoto_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await ConfirmBusPhotoAsync(accessToken, Guid.NewGuid(), Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task ConfirmBusPhoto_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await ConfirmBusPhotoAsync(accessToken, Guid.NewGuid(), Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ConfirmBusPhoto_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database, await TestMediaStore.OverrideAsync());
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/buses/{Guid.NewGuid()}/photos/{Guid.NewGuid()}/confirm", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static readonly byte[] _jpegSignature = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
    private static readonly byte[] _pngSignature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] _gifSignature = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };

    // A file of the given size that starts with the given signature; the rest is zeros.
    private static byte[] FileOf(byte[] signature, int sizeBytes)
    {
        byte[] file = new byte[sizeBytes];
        signature.CopyTo(file, 0);
        return file;
    }

    private static byte[] BadFile(string file)
    {
        switch (file)
        {
            case "4 MB file":
                return FileOf(_jpegSignature, 4 * 1024 * 1024);
            case "PNG file":
                return FileOf(_pngSignature, 2048);
            case "GIF file":
                return FileOf(_gifSignature, 2048);
            default:
                throw new ArgumentException($"Unknown file '{file}'.", nameof(file));
        }
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
            RegistrationNumber = "TC" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
        });
        return busId;
    }

    private async Task<(Guid PhotoId, string FileLocation)> SeedPhotoAsync(
        Guid busId, string format, string status, int? position, DateTimeOffset slotExpiresAt)
    {
        var photoId = Guid.NewGuid();
        string fileLocation = $"buses/{busId}/photos/{photoId}";
        await _database.SeedAsync(InsertPhotoSql, new
        {
            PhotoId = photoId,
            BusId = busId,
            FileLocation = fileLocation,
            Format = format,
            Status = status,
            Position = position,
            SlotExpiresAt = slotExpiresAt,
        });
        return (photoId, fileLocation);
    }

    private async Task<string?> StoredPhotoAsync(Guid photoId)
    {
        return await _database.QueryScalarAsync<string>(SelectPhotoSql, new { PhotoId = photoId });
    }

    private async Task<HttpResponseMessage> ConfirmBusPhotoAsync(string accessToken, Guid busId, Guid photoId)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(_now), await TestMediaStore.OverrideAsync());
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(
            HttpMethod.Post, $"/buses/{busId}/photos/{photoId}/confirm", accessToken);
        return await client.SendAsync(request);
    }
}
