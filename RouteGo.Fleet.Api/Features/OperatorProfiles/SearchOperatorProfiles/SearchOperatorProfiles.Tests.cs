using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.OperatorProfiles.SearchOperatorProfiles;

[Collection(DatabaseCollection.Name)]
public sealed class SearchOperatorProfilesTests
{
    // The search sees every test's profiles, so each test puts its own marker in the names it seeds
    // and searches by it.
    private const string InsertProfileSql = """
        INSERT INTO operator_profiles.operator_profiles (operator_id, company_name, email, rating, approved_at)
        VALUES (@OperatorId, @CompanyName, 'owner@example.in', 4.5, '2026-10-09T09:00:00Z');
        """;

    // Profiles "<marker> Lines 01" to "<marker> Lines <count>".
    private const string InsertNumberedProfilesSql = """
        INSERT INTO operator_profiles.operator_profiles (operator_id, company_name, email, rating, approved_at)
        SELECT gen_random_uuid()::text, @Marker || ' Lines ' || lpad(n::text, 2, '0'), 'owner@example.in', 4.0, '2026-10-09T09:00:00Z'
        FROM generate_series(1, @Count) AS n;
        """;

    private readonly DatabaseFixture _database;

    public SearchOperatorProfilesTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task SearchOperatorProfiles_ByPartOfTheCompanyName_ReturnsMatchesIgnoringCaseSortedByName()
    {
        // Arrange
        string marker = NewMarker();
        Guid konkan = await SeedProfileAsync($"Konkan Busco{marker}");
        Guid busCo = await SeedProfileAsync($"BusCo{marker} Travels");
        await SeedProfileAsync($"Shivneri{marker} Lines");

        // Act
        using JsonDocument page = await SearchPageAsync($"companyName=busco{marker.ToLowerInvariant()}");

        // Assert
        Assert.Equal($"BusCo{marker} Travels,Konkan Busco{marker}", Join(page.RootElement, "companyName"));
        Assert.Equal($"{busCo},{konkan}", Join(page.RootElement, "operatorId"));
        JsonElement profile = page.RootElement.GetProperty("items")[0];
        Assert.Equal("owner@example.in", profile.GetProperty("email").GetString());
        Assert.Equal(4.5m, profile.GetProperty("rating").GetDecimal());
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero), profile.GetProperty("approvedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task SearchOperatorProfiles_FortyFiveProfilesPageThreeOfTwenty_ReturnsTheLastFiveWithTotal()
    {
        // Arrange
        string marker = NewMarker();
        await _database.SeedAsync(InsertNumberedProfilesSql, new { Marker = marker, Count = 45 });

        // Act
        using JsonDocument page = await SearchPageAsync($"companyName={marker}&page=3&pageSize=20");

        // Assert
        Assert.Equal(
            $"{marker} Lines 41,{marker} Lines 42,{marker} Lines 43,{marker} Lines 44,{marker} Lines 45",
            Join(page.RootElement, "companyName"));
        Assert.Equal(45, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, page.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(20, page.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task SearchOperatorProfiles_NoCompanyName_ListsEveryProfileSortedByNameIgnoringCase()
    {
        // Arrange: a leading digit sorts these before every other test's names.
        string marker = NewMarker();
        await SeedProfileAsync($"0{marker} Charlie");
        await SeedProfileAsync($"0{marker} alpha");
        await SeedProfileAsync($"0{marker} Bravo");
        int profileCount = await _database.QueryScalarAsync<int>("SELECT count(*)::int FROM operator_profiles.operator_profiles");

        // Act
        using JsonDocument page = await SearchPageAsync("pageSize=3");

        // Assert
        Assert.Equal($"0{marker} alpha,0{marker} Bravo,0{marker} Charlie", Join(page.RootElement, "companyName"));
        Assert.Equal(profileCount, page.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task SearchOperatorProfiles_NoMatch_ReturnsAnEmptyPage()
    {
        // Act
        using JsonDocument page = await SearchPageAsync("companyName=zzz");

        // Assert
        Assert.Equal(0, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(0, page.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData(151, "", "companyName")]
    [InlineData(10, "&page=0", "page")]
    [InlineData(10, "&pageSize=201", "pageSize")]
    public async Task SearchOperatorProfiles_FilterTooLongOrPageOutOfRange_IsRefusedNamingTheField(int nameLength, string pagingQuery, string field)
    {
        // Arrange
        string companyName = new string('a', nameLength);

        // Act
        using HttpResponseMessage response = await SearchAsync(TestAccessTokens.Create("Admin"), $"companyName={companyName}{pagingQuery}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"No error for {field}.");
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Customer")]
    public async Task SearchOperatorProfiles_NotAnAdmin_IsRefusedForbidden(string role)
    {
        // Act
        using HttpResponseMessage response = await SearchAsync(TestAccessTokens.Create(role), string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task SearchOperatorProfiles_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/admin/operatorProfiles");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    // Upper-case, so a lower-case search proves the match ignores case.
    private static string NewMarker()
    {
        return "Q" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    }

    private static string Join(JsonElement page, string property)
    {
        IEnumerable<string?> values = page.GetProperty("items").EnumerateArray().Select(profile => profile.GetProperty(property).GetString());
        return string.Join(",", values);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Guid> SeedProfileAsync(string companyName)
    {
        var operatorId = Guid.NewGuid();
        await _database.SeedAsync(InsertProfileSql, new { OperatorId = operatorId.ToString(), CompanyName = companyName });
        return operatorId;
    }

    private async Task<JsonDocument> SearchPageAsync(string query)
    {
        using HttpResponseMessage response = await SearchAsync(TestAccessTokens.Create("Admin"), query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SearchAsync(string accessToken, string query)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, $"/admin/operatorProfiles?{query}", accessToken);
        return await client.SendAsync(request);
    }
}
