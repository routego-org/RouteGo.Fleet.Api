using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Buses.ActivateBus;

[Collection(DatabaseCollection.Name)]
public sealed class ActivateBusTests
{
    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Seater', true, NULL, @Status);
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 10, 4, 2);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Seater', 1);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'S1', 'Lower', 1, 1, 'Seater'), (@BusId, 'S2', 'Lower', 1, 2, 'Seater');
        """;

    private const string SelectStatusSql = """
        SELECT status FROM buses.buses WHERE bus_id = @BusId
        """;

    private readonly DatabaseFixture _database;

    public ActivateBusTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ActivateBus_InactiveBus_BecomesActive()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId, "Inactive");

        // Act
        using HttpResponseMessage response = await ActivateBusAsync(TestAccessTokens.CreateOperator(operatorId), busId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bus = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(busId, bus.RootElement.GetProperty("busId").GetGuid());
        Assert.Equal("active", bus.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, bus.RootElement.GetProperty("totalSeats").GetInt32());
        Assert.Equal("Active", await StoredStatusAsync(busId));
    }

    [Fact]
    public async Task ActivateBus_AlreadyActive_ReturnsBusUnchanged()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId, "Active");

        // Act
        using HttpResponseMessage response = await ActivateBusAsync(TestAccessTokens.CreateOperator(operatorId), busId);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bus = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("active", bus.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ActivateBus_UnknownBus_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await ActivateBusAsync(TestAccessTokens.CreateOperator(operatorId), Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ActivateBus_AnotherOperatorsBus_IsReportedNotFoundAndLeftInactive()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(owner, "Inactive");

        // Act
        using HttpResponseMessage response = await ActivateBusAsync(TestAccessTokens.CreateOperator(otherOperator), busId);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal("Inactive", await StoredStatusAsync(busId));
    }

    [Fact]
    public async Task ActivateBus_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await ActivateBusAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task ActivateBus_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await ActivateBusAsync(accessToken, Guid.NewGuid());

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ActivateBus_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/buses/{Guid.NewGuid()}/activate", null);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Guid> SeedBusAsync(Guid operatorId, string status)
    {
        var busId = Guid.NewGuid();
        await _database.SeedAsync(InsertBusSql, new
        {
            BusId = busId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "T" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant(),
            Status = status,
        });
        return busId;
    }

    private async Task<string?> StoredStatusAsync(Guid busId)
    {
        return await _database.QueryScalarAsync<string>(SelectStatusSql, new { BusId = busId });
    }

    private async Task<HttpResponseMessage> ActivateBusAsync(string accessToken, Guid busId)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, $"/buses/{busId}/activate", accessToken);
        return await client.SendAsync(request);
    }
}
