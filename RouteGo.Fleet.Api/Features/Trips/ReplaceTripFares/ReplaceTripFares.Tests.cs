using System.Text.Json;
using RouteGo.Fleet.Api.Common.Behaviors;

namespace RouteGo.Fleet.Api.Features.Trips.ReplaceTripFares;

[Collection(DatabaseCollection.Name)]
public sealed class ReplaceTripFaresTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    // 2026-10-16 at 10:00 IST; T1 departs that day at 22:30 IST (17:00 UTC).
    private static readonly DateTimeOffset _morning = new DateTimeOffset(2026, 10, 16, 4, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _departureAt = new DateTimeOffset(2026, 10, 16, 17, 0, 0, TimeSpan.Zero);

    // Bus "Alpha": 18 "Lower berth" seats L1-L18 on the lower deck and 18 "Upper berth" seats U1-U18
    // on the upper deck; route Mumbai -> Pune; schedule S1 with "Lower berth" Rs 1,200 and
    // "Upper berth" Rs 1,000 (in paise).
    private const string InsertFleetSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Sleeper', true, NULL, 'Active');
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 9, 2, 1), (@BusId, 'Upper', 9, 2, 1);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Lower berth', 1), (@BusId, 'Upper berth', 2);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        SELECT @BusId, d.prefix || ((r - 1) * 2 + c), d.deck, r, c, d.seat_class
        FROM (VALUES ('L', 'Lower', 'Lower berth'), ('U', 'Upper', 'Upper berth')) AS d(prefix, deck, seat_class)
        CROSS JOIN generate_series(1, 9) AS r
        CROSS JOIN generate_series(1, 2) AS c;
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, 'Active');
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Pune, 210);
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '22:30', ARRAY['Friday'], 'Active', '2026-10-29');
        INSERT INTO trips.schedule_class_fares (schedule_id, seat_class, fare)
        VALUES (@ScheduleId, 'Lower berth', 120000), (@ScheduleId, 'Upper berth', 100000);
        """;

    // A trip of S1 at 22:30 IST with the schedule's fares; a cancelled one carries the cause, reason
    // and time the trips table requires of every cancelled trip.
    private const string InsertTripSql = """
        INSERT INTO trips.trips (trip_id, schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status,
                                 cancellation_cause, cancel_reason, cancelled_at)
        VALUES (@TripId, @ScheduleId, @OperatorId, @RouteId, @BusId, @ServiceDate,
                (@ServiceDate + time '22:30') AT TIME ZONE 'Asia/Kolkata',
                (@ServiceDate + time '22:30') AT TIME ZONE 'Asia/Kolkata' + interval '210 minutes',
                CASE WHEN @Cause IS NULL THEN 'Scheduled' ELSE 'Cancelled' END,
                @Cause,
                CASE WHEN @Cause IS NOT NULL THEN 'Bus sent for repair' END,
                CASE WHEN @Cause IS NOT NULL THEN timestamptz '2026-10-08T10:00:00Z' END);
        INSERT INTO trips.trip_class_fares (trip_id, seat_class, fare)
        VALUES (@TripId, 'Lower berth', 120000), (@TripId, 'Upper berth', 100000);
        """;

    private const string TripFaresSql = """
        SELECT string_agg(seat_class || '=' || fare, ',' ORDER BY seat_class)
        FROM trips.trip_class_fares WHERE trip_id = @TripId
        """;

    private const string ScheduleFaresSql = """
        SELECT string_agg(seat_class || '=' || fare, ',' ORDER BY seat_class)
        FROM trips.schedule_class_fares WHERE schedule_id = @ScheduleId
        """;

    // Events of every trip of the schedule, per "type/trip/correlation".
    private const string EventSummarySql = """
        SELECT coalesce(string_agg(e.event_type || '/' || e.trip_id || '/' || e.correlation_id, ','), '-')
        FROM trips.fleet_events e JOIN trips.trips t ON t.trip_id = e.trip_id
        WHERE t.schedule_id = @ScheduleId
        """;

    private const string EventDataSql = """
        SELECT data::text FROM trips.fleet_events WHERE trip_id = @TripId AND event_type = 'FareChanged'
        """;

    private readonly DatabaseFixture _database;

    public ReplaceTripFaresTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ReplaceTripFares_NewFares_ReturnsTheTripWithThem()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet.Token, fleet.TripId, Fares(1500, 1250), _morning);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var trip = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(fleet.TripId, trip.RootElement.GetProperty("tripId").GetGuid());
        Assert.Equal(fleet.ScheduleId, trip.RootElement.GetProperty("scheduleId").GetGuid());
        Assert.Equal("BusCo Travels", trip.RootElement.GetProperty("operatorName").GetString());
        Assert.Equal("Mumbai", trip.RootElement.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal("Pune", trip.RootElement.GetProperty("destination").GetProperty("name").GetString());
        Assert.Equal("Alpha", trip.RootElement.GetProperty("busName").GetString());
        Assert.Equal("2026-10-16", trip.RootElement.GetProperty("serviceDate").GetString());
        Assert.Equal(_departureAt, trip.RootElement.GetProperty("departureAt").GetDateTimeOffset());
        Assert.Equal("scheduled", trip.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "[{\"seatClass\":\"Lower berth\",\"fare\":1500},{\"seatClass\":\"Upper berth\",\"fare\":1250}]",
            trip.RootElement.GetProperty("classFares").GetRawText());
        Assert.Equal("Lower berth=150000,Upper berth=125000", await TripFaresAsync(fleet.TripId));
    }

    [Fact]
    public async Task ReplaceTripFares_NewFares_RecordsFareChangedWithBothClassesAndAllThirtySixSeats()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet.Token, fleet.TripId, Fares(1500, 1250), _morning);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"FareChanged/{fleet.TripId}/fares-test-correlation", await EventSummaryAsync(fleet));
        string? data = await _database.QueryScalarAsync<string>(EventDataSql, new { fleet.TripId });
        using var fareChange = JsonDocument.Parse(data!);
        JsonElement root = fareChange.RootElement;
        JsonElement seats = root.GetProperty("seats");
        Assert.Equal(fleet.TripId, root.GetProperty("tripId").GetGuid());
        Assert.Equal("Lower berth", root.GetProperty("classFares")[0].GetProperty("class").GetString());
        Assert.Equal(1500, root.GetProperty("classFares")[0].GetProperty("fare").GetInt32());
        Assert.Equal("Upper berth", root.GetProperty("classFares")[1].GetProperty("class").GetString());
        Assert.Equal(1250, root.GetProperty("classFares")[1].GetProperty("fare").GetInt32());
        Assert.Equal(36, seats.GetArrayLength());
        Assert.Equal(18, seats.EnumerateArray().Count(seat => seat.GetProperty("fare").GetInt32() == 1500));
        Assert.Equal(18, seats.EnumerateArray().Count(seat => seat.GetProperty("fare").GetInt32() == 1250));
        Assert.Equal("L1", seats[0].GetProperty("seatNo").GetString());
        Assert.Equal("Lower berth", seats[0].GetProperty("class").GetString());
    }

    [Fact]
    public async Task ReplaceTripFares_NewFares_LeavesTheScheduleAndItsOtherTripsUnchanged()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);
        var otherTripId = Guid.NewGuid();
        await SeedTripAsync(fleet, otherTripId, "2026-10-23", null);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet.Token, fleet.TripId, Fares(1500, 1250), _morning);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Lower berth=120000,Upper berth=100000", await _database.QueryScalarAsync<string>(ScheduleFaresSql, new { fleet.ScheduleId }));
        Assert.Equal("Lower berth=120000,Upper berth=100000", await TripFaresAsync(otherTripId));
    }

    [Fact]
    public async Task ReplaceTripFares_ClassNamesInAnotherCase_StoresTheBusSpelling()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);
        FareBody[] fares = new[] { new FareBody(" UPPER BERTH ", 1250), new FareBody("lower berth", 1500) };

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet.Token, fleet.TripId, fares, _morning);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Lower berth=150000,Upper berth=125000", await TripFaresAsync(fleet.TripId));
    }

    [Fact]
    public async Task ReplaceTripFares_SameFares_ReturnsTheTripUnchangedAndRecordsNoEvent()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet.Token, fleet.TripId, Fares(1200, 1000), _morning);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var trip = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "[{\"seatClass\":\"Lower berth\",\"fare\":1200},{\"seatClass\":\"Upper berth\",\"fare\":1000}]",
            trip.RootElement.GetProperty("classFares").GetRawText());
        Assert.Equal("-", await EventSummaryAsync(fleet));
    }

    [Theory]
    [InlineData(11, 1)]
    [InlineData(11, 0)]
    public async Task ReplaceTripFares_InsideTheCutoff_IsRefusedWithTheCutoffAndFaresUnchanged(int hourUtc, int minute)
    {
        // Arrange: 16:31 IST, and exactly 16:30 IST, are not more than 6 hours before 22:30 IST.
        Fleet fleet = await SeedFleetAsync(null);
        var now = new DateTimeOffset(2026, 10, 16, hourUtc, minute, 0, TimeSpan.Zero);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet.Token, fleet.TripId, Fares(1500, 1250), now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("FARE_CHANGE_CUTOFF_PASSED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 11, 0, 0, TimeSpan.Zero), problem.RootElement.GetProperty("cutoffAt").GetDateTimeOffset());
        Assert.Equal("Lower berth=120000,Upper berth=100000", await TripFaresAsync(fleet.TripId));
    }

    [Fact]
    public async Task ReplaceTripFares_JustBeforeTheCutoff_IsAccepted()
    {
        // Arrange: 16:29 IST.
        Fleet fleet = await SeedFleetAsync(null);
        var now = new DateTimeOffset(2026, 10, 16, 10, 59, 0, TimeSpan.Zero);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet.Token, fleet.TripId, Fares(1500, 1250), now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReplaceTripFares_DepartedTrip_IsRefusedTripDeparted()
    {
        // Arrange: 23:00 IST.
        Fleet fleet = await SeedFleetAsync(null);
        var now = new DateTimeOffset(2026, 10, 16, 17, 30, 0, TimeSpan.Zero);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet.Token, fleet.TripId, Fares(1500, 1250), now);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TRIP_DEPARTED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(_departureAt, problem.RootElement.GetProperty("departedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task ReplaceTripFares_CancelledTrip_IsRefusedTripNotScheduledWithItsStatus()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("Operator");

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet.Token, fleet.TripId, Fares(1500, 1250), _morning);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TRIP_NOT_SCHEDULED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("cancelled", problem.RootElement.GetProperty("currentStatus").GetString());
    }

    [Theory]
    [InlineData("Lower berth", null, "[\"Upper berth\"]", "[]")]
    [InlineData("Lower berth", "Window", "[]", "[\"Window\"]")]
    public async Task ReplaceTripFares_FaresNotMatchingTheBus_IsRefusedClassFaresMismatch(
        string firstClass, string? extraClass, string missingClasses, string unknownClasses)
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet.Token, fleet.TripId, MismatchedFares(firstClass, extraClass), _morning);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CLASS_FARES_MISMATCH", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(missingClasses, problem.RootElement.GetProperty("missingClasses").GetRawText());
        Assert.Equal(unknownClasses, problem.RootElement.GetProperty("unknownClasses").GetRawText());
        Assert.Equal("Lower berth=120000,Upper berth=100000", await TripFaresAsync(fleet.TripId));
    }

    [Theory]
    [InlineData("fare 49", "classFares[0].fare")]
    [InlineData("fare 10001", "classFares[0].fare")]
    [InlineData("fare 1200.50", "classFares[0].fare")]
    [InlineData("lower berth twice", "classFares[1].seatClass")]
    [InlineData("no fares", "classFares")]
    public async Task ReplaceTripFares_FareBreakingARule_IsRefusedNamingTheFare(string rule, string field)
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet.Token, fleet.TripId, Breaking(rule), _morning);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"No error for {field}.");
    }

    [Fact]
    public async Task ReplaceTripFares_UnknownTrip_IsRefusedNotFound()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet.Token, Guid.NewGuid(), Fares(1500, 1250), _morning);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ReplaceTripFares_AnotherOperatorsTrip_IsReportedNotFoundAndLeftAlone()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(
            TestAccessTokens.CreateOperator(otherOperator), fleet.TripId, Fares(1500, 1250), _morning);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal("Lower berth=120000,Upper berth=100000", await TripFaresAsync(fleet.TripId));
    }

    [Fact]
    public async Task ReplaceTripFares_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await ReplaceAsync(accessToken, Guid.NewGuid(), Fares(1500, 1250), _morning);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task ReplaceTripFares_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(accessToken, Guid.NewGuid(), Fares(1500, 1250), _morning);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ReplaceTripFares_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PutAsync($"/trips/{Guid.NewGuid()}/fares", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static FareBody[] Fares(decimal lowerBerth, decimal upperBerth)
    {
        return new[] { new FareBody("Lower berth", lowerBerth), new FareBody("Upper berth", upperBerth) };
    }

    private static FareBody[] MismatchedFares(string firstClass, string? extraClass)
    {
        if (extraClass is null)
        {
            return new[] { new FareBody(firstClass, 1500) };
        }

        return new[] { new FareBody(firstClass, 1500), new FareBody("Upper berth", 1250), new FareBody(extraClass, 900) };
    }

    private static FareBody[]? Breaking(string rule)
    {
        switch (rule)
        {
            case "fare 49":
                return Fares(49, 1000);
            case "fare 10001":
                return Fares(10_001, 1000);
            case "fare 1200.50":
                return Fares(1200.50m, 1000);
            case "lower berth twice":
                return new[] { new FareBody("Lower berth", 1500), new FareBody("lower berth", 1250) };
            case "no fares":
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule.");
        }
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Fleet> SeedFleetAsync(string? cause)
    {
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        var fleet = new Fleet
        {
            OperatorId = operatorId,
            BusId = Guid.NewGuid(),
            RouteId = Guid.NewGuid(),
            ScheduleId = Guid.NewGuid(),
            TripId = Guid.NewGuid(),
        };
        await _database.SeedAsync(InsertFleetSql, new
        {
            fleet.BusId,
            fleet.RouteId,
            fleet.ScheduleId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "TF" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            Mumbai = _mumbai,
            Pune = _pune,
        });
        await SeedTripAsync(fleet, fleet.TripId, "2026-10-16", cause);
        return fleet;
    }

    private async Task SeedTripAsync(Fleet fleet, Guid tripId, string serviceDate, string? cause)
    {
        await _database.SeedAsync(InsertTripSql, new
        {
            TripId = tripId,
            fleet.ScheduleId,
            OperatorId = fleet.OperatorId.ToString(),
            fleet.RouteId,
            fleet.BusId,
            ServiceDate = DateOnly.Parse(serviceDate, System.Globalization.CultureInfo.InvariantCulture),
            Cause = cause,
        });
    }

    private async Task<string?> TripFaresAsync(Guid tripId)
    {
        return await _database.QueryScalarAsync<string>(TripFaresSql, new { TripId = tripId });
    }

    private async Task<string?> EventSummaryAsync(Fleet fleet)
    {
        return await _database.QueryScalarAsync<string>(EventSummarySql, new { fleet.ScheduleId });
    }

    private async Task<HttpResponseMessage> ReplaceAsync(string accessToken, Guid tripId, FareBody[]? fares, DateTimeOffset now)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(now));
        using HttpClient client = factory.CreateClient();
        var body = new { ClassFares = fares };
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Put, $"/trips/{tripId}/fares", accessToken, body);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "fares-test-correlation");
        return await client.SendAsync(request);
    }

    /// <summary>An operator with a profile, bus "Alpha", a route, schedule S1 and its trip T1 on 2026-10-16.</summary>
    private sealed class Fleet
    {
        public required Guid OperatorId { get; init; }

        public required Guid BusId { get; init; }

        public required Guid RouteId { get; init; }

        public required Guid ScheduleId { get; init; }

        public required Guid TripId { get; init; }

        public string Token
        {
            get { return TestAccessTokens.CreateOperator(OperatorId); }
        }
    }

    private sealed record FareBody(string SeatClass, decimal Fare);
}
