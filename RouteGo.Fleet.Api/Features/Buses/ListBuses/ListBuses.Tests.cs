using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Buses.ListBuses;

[Collection(DatabaseCollection.Name)]
public sealed class ListBusesTests
{
    // The local CDN base address every test host is configured with.
    private const string CdnBaseUrl = "http://localhost:5002/media/";

    // One lower deck of 6 rows by 3 columns, filled seat by seat up to @SeatCount.
    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, @Name, 'SemiSleeper', true, ARRAY[]::text[], @Status);
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 6, 3, 1);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Seater', 1);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        SELECT @BusId, 'S' || n, 'Lower', ((n - 1) / 3) + 1, ((n - 1) % 3) + 1, 'Seater'
        FROM generate_series(1, @SeatCount) AS n;
        """;

    private const string InsertPhotoSql = """
        INSERT INTO buses.bus_photos (photo_id, bus_id, file_location, format, size_bytes, status, position, slot_expires_at, uploaded_at)
        VALUES (
            @PhotoId, @BusId, @FileLocation, 'Jpeg', 1000, @Status, @Position, now() + interval '5 minutes',
            CASE WHEN @Status = 'Uploaded' THEN now() END);
        """;

    private readonly DatabaseFixture _database;

    public ListBusesTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ListBuses_ActiveAndInactiveOwnBuses_ReturnsBothByNameButNotAnotherOperatorsBus()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        await SeedBusAsync(operatorId, "Bravo", "Inactive", 12);
        string alphaRegistration = await SeedBusAsync(operatorId, "Alpha", "Active", 36);
        await SeedBusAsync(otherOperator, "Charlie", "Active", 36);

        // Act
        using JsonDocument page = await ListAsync(operatorId, string.Empty);

        // Assert
        JsonElement items = page.RootElement.GetProperty("items");
        Assert.Equal(new[] { "Alpha", "Bravo" }, Names(items));
        Assert.Equal(new[] { "active", "inactive" }, Statuses(items));
        Assert.Equal(2, page.RootElement.GetProperty("totalCount").GetInt32());
        JsonElement alpha = items[0];
        Assert.Equal(alphaRegistration, alpha.GetProperty("registrationNumber").GetString());
        Assert.Equal("semiSleeper", alpha.GetProperty("busType").GetString());
        Assert.True(alpha.GetProperty("isAc").GetBoolean());
        Assert.Equal(36, alpha.GetProperty("totalSeats").GetInt32());
        Assert.Equal(12, items[1].GetProperty("totalSeats").GetInt32());
        Assert.Equal(JsonValueKind.Null, alpha.GetProperty("coverPhotoUrl").ValueKind);
    }

    [Fact]
    public async Task ListBuses_StatusInactive_ReturnsOnlyTheInactiveBus()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        await SeedBusAsync(operatorId, "Alpha", "Active", 36);
        await SeedBusAsync(operatorId, "Bravo", "Inactive", 36);

        // Act
        using JsonDocument page = await ListAsync(operatorId, "?status=inactive");

        // Assert
        Assert.Equal(new[] { "Bravo" }, Names(page.RootElement.GetProperty("items")));
        Assert.Equal(1, page.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ListBuses_UploadedPhotosAndAPendingSlot_CoverIsTheFirstUploadedPhotoOnTheCdn()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        string registration = await SeedBusAsync(operatorId, "Alpha", "Active", 36);
        Guid busId = await BusIdAsync(registration);
        await SeedPhotoAsync(busId, "Pending", null);
        await SeedPhotoAsync(busId, "Uploaded", 2);
        string coverLocation = await SeedPhotoAsync(busId, "Uploaded", 1);

        // Act
        using JsonDocument page = await ListAsync(operatorId, string.Empty);

        // Assert
        JsonElement alpha = page.RootElement.GetProperty("items")[0];
        Assert.Equal(CdnBaseUrl + coverLocation, alpha.GetProperty("coverPhotoUrl").GetString());
    }

    [Fact]
    public async Task ListBuses_NoBusesYet_ReturnsAnEmptyPageWithTotalCountZero()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using JsonDocument page = await ListAsync(operatorId, string.Empty);

        // Assert
        Assert.Equal(0, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(0, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(20, page.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData(1, new[] { "alpha", "Bravo" })]
    [InlineData(2, new[] { "charlie" })]
    public async Task ListBuses_PagesOfTwo_AreSortedByNameIgnoringCase(int pageNumber, string[] expectedNames)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        await SeedBusAsync(operatorId, "charlie", "Active", 36);
        await SeedBusAsync(operatorId, "Bravo", "Active", 36);
        await SeedBusAsync(operatorId, "alpha", "Active", 36);

        // Act
        using JsonDocument page = await ListAsync(operatorId, $"?page={pageNumber}&pageSize=2");

        // Assert
        Assert.Equal(expectedNames, Names(page.RootElement.GetProperty("items")));
        Assert.Equal(3, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(pageNumber, page.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(2, page.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("?status=retired", "status")]
    [InlineData("?status=Inactive", "status")]
    [InlineData("?page=0", "page")]
    [InlineData("?pageSize=0", "pageSize")]
    [InlineData("?pageSize=201", "pageSize")]
    public async Task ListBuses_InvalidQuery_IsRefusedValidationFailedNamingTheField(string query, string field)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await SendListAsync(TestAccessTokens.CreateOperator(operatorId), query);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        IEnumerable<string> fields = problem.RootElement.GetProperty("errors").EnumerateObject().Select(error => error.Name);
        Assert.Contains(field, fields);
    }

    [Fact]
    public async Task ListBuses_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await SendListAsync(accessToken, string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task ListBuses_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await SendListAsync(accessToken, string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ListBuses_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/buses");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static string?[] Names(JsonElement items)
    {
        JsonElement.ArrayEnumerator buses = items.EnumerateArray();
        return buses.Select(bus => bus.GetProperty("name").GetString()).ToArray();
    }

    private static string?[] Statuses(JsonElement items)
    {
        JsonElement.ArrayEnumerator buses = items.EnumerateArray();
        return buses.Select(bus => bus.GetProperty("status").GetString()).ToArray();
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    // Registration numbers are unique across every operator, so each bus gets a fresh one.
    private async Task<string> SeedBusAsync(Guid operatorId, string name, string status, int seatCount)
    {
        string registrationNumber = "T" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant();
        await _database.SeedAsync(InsertBusSql, new
        {
            BusId = Guid.NewGuid(),
            OperatorId = operatorId.ToString(),
            RegistrationNumber = registrationNumber,
            Name = name,
            Status = status,
            SeatCount = seatCount,
        });
        return registrationNumber;
    }

    private async Task<Guid> BusIdAsync(string registrationNumber)
    {
        return await _database.QueryScalarAsync<Guid>(
            "SELECT bus_id FROM buses.buses WHERE registration_number = @RegistrationNumber",
            new { RegistrationNumber = registrationNumber });
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

    private async Task<JsonDocument> ListAsync(Guid operatorId, string query)
    {
        using HttpResponseMessage response = await SendListAsync(TestAccessTokens.CreateOperator(operatorId), query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SendListAsync(string accessToken, string query)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, $"/buses{query}", accessToken);
        return await client.SendAsync(request);
    }
}
