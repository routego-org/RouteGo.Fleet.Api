using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RouteGo.Fleet.Api.Infrastructure.Pgsql;

namespace RouteGo.Fleet.Api.Features.OperatorProfiles.CreateOperatorProfile;

[Collection(DatabaseCollection.Name)]
public sealed class CreateOperatorProfileTests
{
    // Nothing listens on port 1, so every attempt to reach this database fails the way an outage does.
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=fleet;Username=fleet;Password=fleet;Timeout=1";

    // The profile as "company|email|rating|approved time|created by".
    private const string ProfileSql = """
        SELECT company_name || '|' || email || '|' || rating::text || '|'
               || to_char(approved_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS') || '|' || created_by_id
        FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId
        """;

    private const string CountProfilesSql = "SELECT count(*) FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId";

    private static readonly DateTimeOffset _approvedAt = new DateTimeOffset(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    private readonly DatabaseFixture _database;
    private readonly CapturingLogger _logger = new CapturingLogger();

    public CreateOperatorProfileTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task HandleAsync_ApprovedOperator_CreatesProfileWithDefaultRatingAndApprovalTime()
    {
        // Arrange
        string operatorId = Guid.NewGuid().ToString();
        OperatorApprovedDelivery delivery = Delivery("E1", Data(operatorId, "BusCo Travels", "owner@busco.in"));

        // Act
        CreateOperatorProfileOutcome outcome = await Handler(_database.ConnectionString).HandleAsync(delivery, CancellationToken.None);

        // Assert
        Assert.Equal(CreateOperatorProfileOutcome.Created, outcome);
        Assert.Equal(
            "BusCo Travels|owner@busco.in|4.0|2026-10-09T09:00:00|" + SystemActor.Default.Id,
            await ProfileAsync(operatorId));
    }

    [Fact]
    public async Task HandleAsync_ApprovedOperator_OperatorCanThenCreateABus()
    {
        // Arrange
        var operatorId = Guid.NewGuid();
        OperatorApprovedDelivery delivery = Delivery("E1", Data(operatorId.ToString(), "BusCo Travels", "owner@busco.in"));
        await Handler(_database.ConnectionString).HandleAsync(delivery, CancellationToken.None);

        // Act
        using HttpResponseMessage response = await CreateBusAsync(TestAccessTokens.CreateOperator(operatorId));

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_SameEventDeliveredAgain_KeepsExactlyOneUnchangedProfile()
    {
        // Arrange
        string operatorId = Guid.NewGuid().ToString();
        OperatorApprovedDelivery delivery = Delivery("E7", Data(operatorId, "BusCo Travels", "owner@busco.in"));
        CreateOperatorProfileHandler handler = Handler(_database.ConnectionString);
        await handler.HandleAsync(delivery, CancellationToken.None);
        string? before = await ProfileAsync(operatorId);

        // Act
        CreateOperatorProfileOutcome outcome = await handler.HandleAsync(delivery, CancellationToken.None);

        // Assert
        Assert.Equal(CreateOperatorProfileOutcome.AlreadyExists, outcome);
        Assert.Equal(1L, await _database.QueryScalarAsync<long>(CountProfilesSql, new { OperatorId = operatorId }));
        Assert.Equal(before, await ProfileAsync(operatorId));
    }

    [Fact]
    public async Task HandleAsync_DifferentApprovalEventForTheSameOperator_ChangesNothing()
    {
        // Arrange
        string operatorId = Guid.NewGuid().ToString();
        CreateOperatorProfileHandler handler = Handler(_database.ConnectionString);
        await handler.HandleAsync(Delivery("E1", Data(operatorId, "BusCo Travels", "owner@busco.in")), CancellationToken.None);
        OperatorApprovedDelivery second = Delivery("E2", Data(operatorId, "BusCo Travels Pvt Ltd", "accounts@busco.in"));

        // Act
        CreateOperatorProfileOutcome outcome = await handler.HandleAsync(second, CancellationToken.None);

        // Assert
        Assert.Equal(CreateOperatorProfileOutcome.AlreadyExists, outcome);
        Assert.Equal(1L, await _database.QueryScalarAsync<long>(CountProfilesSql, new { OperatorId = operatorId }));
        Assert.StartsWith("BusCo Travels|owner@busco.in|", await ProfileAsync(operatorId));
    }

    [Theory]
    [InlineData("no operator identifier")]
    [InlineData("no company name")]
    [InlineData("no email")]
    [InlineData("blank email")]
    [InlineData("company name of 1 character")]
    [InlineData("company name of 151 characters")]
    [InlineData("data that is not an object")]
    public async Task HandleAsync_UnusableEventData_CreatesNoProfileAndRaisesTheAlertOnce(string scenario)
    {
        // Arrange
        string operatorId = Guid.NewGuid().ToString();
        OperatorApprovedDelivery delivery = Delivery("E9", UnusableData(scenario, operatorId));

        // Act
        CreateOperatorProfileOutcome outcome = await Handler(_database.ConnectionString).HandleAsync(delivery, CancellationToken.None);

        // Assert
        Assert.Equal(CreateOperatorProfileOutcome.InvalidEventData, outcome);
        Assert.Equal(0L, await _database.QueryScalarAsync<long>(CountProfilesSql, new { OperatorId = operatorId }));
        CapturedLog alert = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.StartsWith("OPERATOR_PROFILE_NOT_CREATED", alert.Message);
        Assert.Contains("E9", alert.Message);
        Assert.Contains("invalidEventData", alert.Message);
    }

    [Fact]
    public async Task HandleAsync_DatabaseUnavailableBeforeTheLastAttempt_AsksForRedeliveryWithoutAlert()
    {
        // Arrange
        OperatorApprovedDelivery delivery = Delivery("E7", Data(Guid.NewGuid().ToString(), "BusCo Travels", "owner@busco.in"));

        // Act
        CreateOperatorProfileOutcome outcome = await Handler(UnreachableDatabase).HandleAsync(delivery, CancellationToken.None);

        // Assert
        Assert.Equal(CreateOperatorProfileOutcome.RetryLater, outcome);
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task HandleAsync_DatabaseUnavailableOnTheLastAttempt_AsksForRedeliveryAndRaisesTheAlert()
    {
        // Arrange
        OperatorApprovedDelivery delivery = Delivery("E7", Data(Guid.NewGuid().ToString(), "BusCo Travels", "owner@busco.in")) with
        {
            IsLastAttempt = true,
        };

        // Act
        CreateOperatorProfileOutcome outcome = await Handler(UnreachableDatabase).HandleAsync(delivery, CancellationToken.None);

        // Assert
        Assert.Equal(CreateOperatorProfileOutcome.RetryLater, outcome);
        CapturedLog alert = Assert.Single(_logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.StartsWith("OPERATOR_PROFILE_NOT_CREATED", alert.Message);
        Assert.Contains("E7", alert.Message);
        Assert.Contains("repeatedFailure", alert.Message);
    }

    private static OperatorApprovedDelivery Delivery(string eventId, JsonElement data)
    {
        return new OperatorApprovedDelivery
        {
            EventId = eventId,
            OccurredAt = _approvedAt,
            CorrelationId = "approve-test-correlation",
            Data = data,
            IsLastAttempt = false,
        };
    }

    private static JsonElement Data(string operatorId, string companyName, string email)
    {
        var data = new JsonObject { ["operatorId"] = operatorId, ["companyName"] = companyName, ["email"] = email };
        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonElement UnusableData(string scenario, string operatorId)
    {
        var data = new JsonObject { ["operatorId"] = operatorId, ["companyName"] = "BusCo Travels", ["email"] = "owner@busco.in" };
        switch (scenario)
        {
            case "no operator identifier":
                data.Remove("operatorId");
                break;
            case "no company name":
                data.Remove("companyName");
                break;
            case "no email":
                data.Remove("email");
                break;
            case "blank email":
                data["email"] = "  ";
                break;
            case "company name of 1 character":
                data["companyName"] = "B";
                break;
            case "company name of 151 characters":
                data["companyName"] = new string('B', 151);
                break;
            case "data that is not an object":
                return JsonSerializer.SerializeToElement("BusCo Travels");
            default:
                throw new ArgumentException($"Unknown scenario '{scenario}'.", nameof(scenario));
        }

        return JsonSerializer.SerializeToElement(data);
    }

    private static JsonObject BusBody()
    {
        var seats = new JsonArray
        {
            Seat("L1", "lower", 1, 1, "Lower berth"),
            Seat("L2", "lower", 1, 2, "Lower berth"),
            Seat("U1", "upper", 1, 1, "Upper berth"),
            Seat("U2", "upper", 1, 2, "Upper berth"),
        };
        return new JsonObject
        {
            ["registrationNumber"] = "OP" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            ["name"] = "Shivneri Sleeper",
            ["busType"] = "sleeper",
            ["isAc"] = true,
            ["amenities"] = new JsonArray(),
            ["seatClasses"] = new JsonArray("Lower berth", "Upper berth"),
            ["layout"] = new JsonObject
            {
                ["decks"] = new JsonArray(Deck("lower"), Deck("upper")),
                ["seats"] = seats,
            },
        };
    }

    private static JsonObject Deck(string deck)
    {
        return new JsonObject { ["deck"] = deck, ["rows"] = 1, ["columns"] = 2, ["aisleAfterColumn"] = 1 };
    }

    private static JsonObject Seat(string label, string deck, int row, int column, string seatClass)
    {
        return new JsonObject { ["label"] = label, ["deck"] = deck, ["row"] = row, ["column"] = column, ["seatClass"] = seatClass };
    }

    private CreateOperatorProfileHandler Handler(string connectionString)
    {
        var writerFactory = new WriterConnectionFactory(connectionString, SystemActor.Default, ConnectionRetryOptions.ManagedInstances);
        return new CreateOperatorProfileHandler(writerFactory, _logger);
    }

    private async Task<string?> ProfileAsync(string operatorId)
    {
        return await _database.QueryScalarAsync<string>(ProfileSql, new { OperatorId = operatorId });
    }

    private async Task<HttpResponseMessage> CreateBusAsync(string accessToken)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, "/buses", accessToken, BusBody());
        return await client.SendAsync(request);
    }
}
