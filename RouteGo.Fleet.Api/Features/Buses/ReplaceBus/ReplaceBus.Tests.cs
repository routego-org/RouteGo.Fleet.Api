using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouteGo.Fleet.Api.Features.Buses.ReplaceBus;

[Collection(DatabaseCollection.Name)]
public sealed class ReplaceBusTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 10, 8, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _nextDepartureAt = new DateTimeOffset(2026, 10, 10, 16, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _departedAt = new DateTimeOffset(2026, 10, 9, 16, 30, 0, TimeSpan.Zero);

    // The bus SmallBody describes: a sleeper with two 2x2 decks, aisle after column 1, eight berths.
    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Sleeper', true, ARRAY['Blanket'], 'Active');
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 2, 2, 1), (@BusId, 'Upper', 2, 2, 1);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Lower berth', 1), (@BusId, 'Upper berth', 2);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'L1', 'Lower', 1, 1, 'Lower berth'), (@BusId, 'L2', 'Lower', 1, 2, 'Lower berth'),
               (@BusId, 'L3', 'Lower', 2, 1, 'Lower berth'), (@BusId, 'L4', 'Lower', 2, 2, 'Lower berth'),
               (@BusId, 'U5', 'Upper', 1, 1, 'Upper berth'), (@BusId, 'U6', 'Upper', 1, 2, 'Upper berth'),
               (@BusId, 'U7', 'Upper', 2, 1, 'Upper berth'), (@BusId, 'U8', 'Upper', 2, 2, 'Upper berth');
        """;

    // A route and schedule for the bus, then one trip per departure. A cancelled trip carries the
    // cause, reason and time the trips table requires of every cancelled trip.
    private const string InsertTripsSql = """
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, 'Active');
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Pune, 210);
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '22:00', ARRAY['Monday'], 'Active', '2026-12-31');
        INSERT INTO trips.trips (schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status,
                                 cancellation_cause, cancel_reason, cancelled_at)
        SELECT @ScheduleId, @OperatorId, @RouteId, @BusId, (t.departure_at AT TIME ZONE 'Asia/Kolkata')::date,
               t.departure_at, t.departure_at + interval '210 minutes', t.status,
               CASE WHEN t.status = 'Cancelled' THEN 'Operator' END,
               CASE WHEN t.status = 'Cancelled' THEN 'Bus sent for repair' END,
               CASE WHEN t.status = 'Cancelled' THEN t.departure_at - interval '1 day' END
        FROM unnest(@Departures, @Statuses) AS t(departure_at, status);
        """;

    private const string SelectNameSql = "SELECT name FROM buses.buses WHERE bus_id = @BusId";

    private const string CountSeatsSql = "SELECT count(*) FROM buses.bus_seats WHERE bus_id = @BusId";

    private const string CountScheduledTripsSql = """
        SELECT count(*) FROM trips.trips WHERE bus_id = @BusId AND status = 'Scheduled'
        """;

    private readonly DatabaseFixture _database;

    public ReplaceBusTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ReplaceBus_FreeDetailsWhileInUse_AreUpdatedAndTripsLeftAlone()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        (Guid busId, string registrationNumber) = await SeedBusAsync(operatorId);
        await SeedFutureTripsAsync(operatorId, busId, 10);
        JsonObject body = SmallBody(registrationNumber);
        body["name"] = "Alpha Plus";
        body["isAc"] = false;
        body["amenities"] = new JsonArray("wifi");

        // Act
        using HttpResponseMessage response = await ReplaceBusAsync(TestAccessTokens.CreateOperator(operatorId), busId, body);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bus = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Alpha Plus", bus.RootElement.GetProperty("name").GetString());
        Assert.False(bus.RootElement.GetProperty("isAc").GetBoolean());
        Assert.Equal("wifi", bus.RootElement.GetProperty("amenities")[0].GetString());
        Assert.Equal(10L, await _database.QueryScalarAsync<long>(CountScheduledTripsSql, new { BusId = busId }));
    }

    [Fact]
    public async Task ReplaceBus_SameLayoutInOtherCaseAndOrderWhileInUse_IsAllowedAndKeepsStoredSpelling()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        (Guid busId, string registrationNumber) = await SeedBusAsync(operatorId);
        await SeedFutureTripsAsync(operatorId, busId, 3);
        JsonObject body = SmallBody(registrationNumber);
        body["seatClasses"] = new JsonArray("upper BERTH", "lower berth");
        body["layout"]!["seats"]![0]!["label"] = "l1";
        body["layout"]!["seats"]![0]!["seatClass"] = "LOWER BERTH";

        // Act
        using HttpResponseMessage response = await ReplaceBusAsync(TestAccessTokens.CreateOperator(operatorId), busId, body);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bus = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Lower berth", bus.RootElement.GetProperty("seatClasses")[0].GetString());
        Assert.Equal("Upper berth", bus.RootElement.GetProperty("seatClasses")[1].GetString());
        Assert.Equal("L1", bus.RootElement.GetProperty("layout").GetProperty("seats")[0].GetProperty("label").GetString());
    }

    [Theory]
    [InlineData("bus type")]
    [InlineData("seat classes")]
    [InlineData("layout")]
    public async Task ReplaceBus_LockedDetailChangedWhileInUse_IsRefusedBusInUseAndNothingChanges(string change)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        (Guid busId, string registrationNumber) = await SeedBusAsync(operatorId);
        await SeedFutureTripsAsync(operatorId, busId, 10);
        JsonObject body = LockedChangeBody(change, registrationNumber);

        // Act
        using HttpResponseMessage response = await ReplaceBusAsync(TestAccessTokens.CreateOperator(operatorId), busId, body);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BUS_IN_USE", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(10, problem.RootElement.GetProperty("futureTripCount").GetInt32());
        Assert.Equal(_nextDepartureAt, problem.RootElement.GetProperty("nextDepartureAt").GetDateTimeOffset());
        Assert.Equal("Alpha", await _database.QueryScalarAsync<string>(SelectNameSql, new { BusId = busId }));
        Assert.Equal(8L, await _database.QueryScalarAsync<long>(CountSeatsSql, new { BusId = busId }));
    }

    [Fact]
    public async Task ReplaceBus_NewLayoutWithOnlyDepartedAndCancelledTrips_IsReplaced()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        (Guid busId, string registrationNumber) = await SeedBusAsync(operatorId);
        await SeedTripsAsync(operatorId, busId, new[] { _departedAt, _nextDepartureAt }, new[] { "Scheduled", "Cancelled" });
        JsonObject body = LockedChangeBody("layout", registrationNumber);

        // Act
        using HttpResponseMessage response = await ReplaceBusAsync(TestAccessTokens.CreateOperator(operatorId), busId, body);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bus = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(12, bus.RootElement.GetProperty("totalSeats").GetInt32());
        Assert.Equal(12L, await _database.QueryScalarAsync<long>(CountSeatsSql, new { BusId = busId }));
    }

    [Fact]
    public async Task ReplaceBus_RegistrationNumberOfAnotherBus_IsRefusedAndNothingChanges()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        (Guid busId, string registrationNumber) = await SeedBusAsync(operatorId);
        (Guid _, string takenNumber) = await SeedBusAsync(otherOperator);
        JsonObject body = SmallBody(registrationNumber);
        body["registrationNumber"] = $"{takenNumber[..2].ToLowerInvariant()}-{takenNumber[2..]}";
        body["name"] = "Alpha Plus";

        // Act
        using HttpResponseMessage response = await ReplaceBusAsync(TestAccessTokens.CreateOperator(operatorId), busId, body);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("BUS_REGISTRATION_ALREADY_EXISTS", await ProblemCodeAsync(response));
        Assert.Equal("Alpha", await _database.QueryScalarAsync<string>(SelectNameSql, new { BusId = busId }));
    }

    [Theory]
    [InlineData("label used twice ignoring case", "layout.seats[1].label")]
    [InlineData("seat outside the deck's rows", "layout.seats[0].row")]
    [InlineData("two seats in one place", "layout.seats[1].column")]
    [InlineData("seat class the bus lacks", "layout.seats[0].seatClass")]
    [InlineData("seat class with no seat", "seatClasses[1]")]
    [InlineData("61 seats", "layout.seats")]
    [InlineData("no lower deck", "layout.decks")]
    [InlineData("aisle after the last column", "layout.decks[0].aisleAfterColumn")]
    [InlineData("registration number of 5 characters", "registrationNumber")]
    [InlineData("name of 1 character", "name")]
    [InlineData("6 seat classes", "seatClasses")]
    [InlineData("seat classes differing only in case", "seatClasses[1]")]
    [InlineData("amenity listed twice", "amenities")]
    [InlineData("amenity not in the list", "amenities")]
    public async Task ReplaceBus_FieldOrLayoutBreaksARule_IsRefusedNamingTheFieldAndNothingChanges(string scenario, string field)
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        (Guid busId, string registrationNumber) = await SeedBusAsync(operatorId);
        JsonObject body = InvalidBody(scenario, registrationNumber);

        // Act
        using HttpResponseMessage response = await ReplaceBusAsync(TestAccessTokens.CreateOperator(operatorId), busId, body);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        IEnumerable<string> fields = problem.RootElement.GetProperty("errors").EnumerateObject().Select(error => error.Name);
        Assert.Contains(fields, name => name.Contains(field, StringComparison.Ordinal));
        Assert.Equal(8L, await _database.QueryScalarAsync<long>(CountSeatsSql, new { BusId = busId }));
    }

    [Fact]
    public async Task ReplaceBus_UnknownBus_IsRefusedNotFound()
    {
        // Arrange
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await ReplaceBusAsync(
            TestAccessTokens.CreateOperator(operatorId), Guid.NewGuid(), SmallBody(UniqueRegistrationNumber()));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ReplaceBus_AnotherOperatorsBus_IsReportedNotFoundAndNothingChanges()
    {
        // Arrange
        Guid owner = await TestOperators.SeedProfileAsync(_database);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        (Guid busId, string registrationNumber) = await SeedBusAsync(owner);
        JsonObject body = SmallBody(registrationNumber);
        body["name"] = "Taken over";

        // Act
        using HttpResponseMessage response = await ReplaceBusAsync(TestAccessTokens.CreateOperator(otherOperator), busId, body);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal("Alpha", await _database.QueryScalarAsync<string>(SelectNameSql, new { BusId = busId }));
    }

    [Fact]
    public async Task ReplaceBus_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await ReplaceBusAsync(accessToken, Guid.NewGuid(), SmallBody(UniqueRegistrationNumber()));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task ReplaceBus_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await ReplaceBusAsync(accessToken, Guid.NewGuid(), SmallBody(UniqueRegistrationNumber()));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ReplaceBus_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.PutAsJsonAsync($"/buses/{Guid.NewGuid()}", SmallBody(UniqueRegistrationNumber()));

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    // Letters and digits: a fresh, valid registration number, so tests sharing the database never collide.
    private static string UniqueRegistrationNumber()
    {
        return "TR" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
    }

    // The seeded bus, described in full: a sleeper with two 2x2 decks, aisle after column 1, eight berths.
    private static JsonObject SmallBody(string registrationNumber)
    {
        var seats = new JsonArray();
        AddGridSeats(seats, "lower", "L", 2, 2, "Lower berth");
        AddGridSeats(seats, "upper", "U", 2, 2, "Upper berth");
        return new JsonObject
        {
            ["registrationNumber"] = registrationNumber,
            ["name"] = "Alpha",
            ["busType"] = "sleeper",
            ["isAc"] = true,
            ["amenities"] = new JsonArray("blanket"),
            ["seatClasses"] = new JsonArray("Lower berth", "Upper berth"),
            ["layout"] = new JsonObject
            {
                ["decks"] = new JsonArray(Deck("lower", 2, 2, 1), Deck("upper", 2, 2, 1)),
                ["seats"] = seats,
            },
        };
    }

    private static JsonObject LockedChangeBody(string change, string registrationNumber)
    {
        JsonObject body = SmallBody(registrationNumber);
        body["name"] = "Alpha Plus";
        switch (change)
        {
            case "bus type":
                body["busType"] = "semiSleeper";
                break;
            case "seat classes":
                body["seatClasses"] = new JsonArray("Lower berth", "Sleeper");
                RenameSeatClass(body, "Upper berth", "Sleeper");
                break;
            case "layout":
                var seats = new JsonArray();
                AddGridSeats(seats, "lower", "L", 3, 2, "Lower berth");
                AddGridSeats(seats, "upper", "U", 3, 2, "Upper berth");
                body["layout"] = new JsonObject
                {
                    ["decks"] = new JsonArray(Deck("lower", 3, 2, 1), Deck("upper", 3, 2, 1)),
                    ["seats"] = seats,
                };
                break;
            default:
                throw new ArgumentException($"Unknown change '{change}'.", nameof(change));
        }

        return body;
    }

    private static void RenameSeatClass(JsonObject body, string from, string to)
    {
        foreach (JsonNode? seat in body["layout"]!["seats"]!.AsArray())
        {
            if (seat!["seatClass"]!.GetValue<string>() == from)
            {
                seat["seatClass"] = to;
            }
        }
    }

    private static JsonObject InvalidBody(string scenario, string registrationNumber)
    {
        JsonObject body = SmallBody(registrationNumber);
        JsonArray seats = body["layout"]!["seats"]!.AsArray();
        JsonArray decks = body["layout"]!["decks"]!.AsArray();
        switch (scenario)
        {
            case "label used twice ignoring case":
                seats[0]!["label"] = "1A";
                seats[1]!["label"] = "1a";
                break;
            case "seat outside the deck's rows":
                decks[0]!["rows"] = 6;
                seats[0]!["row"] = 7;
                break;
            case "two seats in one place":
                seats[1]!["row"] = seats[0]!["row"]!.GetValue<int>();
                seats[1]!["column"] = seats[0]!["column"]!.GetValue<int>();
                break;
            case "seat class the bus lacks":
                seats[0]!["seatClass"] = "Window";
                break;
            case "seat class with no seat":
                var lowerSeats = new JsonArray();
                AddGridSeats(lowerSeats, "lower", "L", 2, 2, "Lower berth");
                body["layout"]!["seats"] = lowerSeats;
                break;
            case "61 seats":
                var manySeats = new JsonArray();
                AddGridSeats(manySeats, "lower", "S", 61, 1, "Lower berth");
                body["layout"]!["seats"] = manySeats;
                break;
            case "no lower deck":
                decks[0]!["deck"] = "upper";
                break;
            case "aisle after the last column":
                decks[0]!["columns"] = 3;
                decks[0]!["aisleAfterColumn"] = 3;
                break;
            case "registration number of 5 characters":
                body["registrationNumber"] = "MH-12A";
                break;
            case "name of 1 character":
                body["name"] = "A";
                break;
            case "6 seat classes":
                body["seatClasses"] = new JsonArray("Lower berth", "Upper berth", "Window", "Aisle", "Middle", "Sixth");
                break;
            case "seat classes differing only in case":
                body["seatClasses"] = new JsonArray("Lower berth", "lower berth");
                break;
            case "amenity listed twice":
                body["amenities"] = new JsonArray("wifi", "wifi");
                break;
            case "amenity not in the list":
                body["amenities"] = new JsonArray("jacuzzi");
                break;
            default:
                throw new ArgumentException($"Unknown scenario '{scenario}'.", nameof(scenario));
        }

        return body;
    }

    private static JsonObject Deck(string deck, int rows, int columns, int? aisleAfterColumn)
    {
        return new JsonObject
        {
            ["deck"] = deck,
            ["rows"] = rows,
            ["columns"] = columns,
            ["aisleAfterColumn"] = aisleAfterColumn,
        };
    }

    private static void AddGridSeats(JsonArray seats, string deck, string prefix, int rows, int columns, string seatClass)
    {
        for (int row = 1; row <= rows; row++)
        {
            for (int column = 1; column <= columns; column++)
            {
                seats.Add(new JsonObject
                {
                    ["label"] = $"{prefix}{seats.Count + 1}",
                    ["deck"] = deck,
                    ["row"] = row,
                    ["column"] = column,
                    ["seatClass"] = seatClass,
                });
            }
        }
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<(Guid BusId, string RegistrationNumber)> SeedBusAsync(Guid operatorId)
    {
        var busId = Guid.NewGuid();
        string registrationNumber = UniqueRegistrationNumber();
        await _database.SeedAsync(InsertBusSql, new
        {
            BusId = busId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = registrationNumber,
        });
        return (busId, registrationNumber);
    }

    // One scheduled trip a day from the next departure on, every one of them still to come.
    private async Task SeedFutureTripsAsync(Guid operatorId, Guid busId, int count)
    {
        var departures = new DateTimeOffset[count];
        string[] statuses = new string[count];
        for (int day = 0; day < count; day++)
        {
            departures[day] = _nextDepartureAt.AddDays(day);
            statuses[day] = "Scheduled";
        }

        await SeedTripsAsync(operatorId, busId, departures, statuses);
    }

    private async Task SeedTripsAsync(Guid operatorId, Guid busId, DateTimeOffset[] departures, string[] statuses)
    {
        await _database.SeedAsync(InsertTripsSql, new
        {
            RouteId = Guid.NewGuid(),
            ScheduleId = Guid.NewGuid(),
            OperatorId = operatorId.ToString(),
            BusId = busId,
            Mumbai = _mumbai,
            Pune = _pune,
            Departures = departures,
            Statuses = statuses,
        });
    }

    private async Task<HttpResponseMessage> ReplaceBusAsync(string accessToken, Guid busId, JsonObject body)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(_now));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Put, $"/buses/{busId}", accessToken, body);
        return await client.SendAsync(request);
    }
}
