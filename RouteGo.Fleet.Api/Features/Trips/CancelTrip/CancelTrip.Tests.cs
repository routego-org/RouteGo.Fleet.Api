using System.Text.Json;
using RouteGo.Fleet.Api.Common.Behaviors;

namespace RouteGo.Fleet.Api.Features.Trips.CancelTrip;

[Collection(DatabaseCollection.Name)]
public sealed class CancelTripTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    // 2026-10-16 at 20:30 IST: T1 departs that day at 22:30 IST (17:00 UTC), in 2 hours.
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _departureAt = new DateTimeOffset(2026, 10, 16, 17, 0, 0, TimeSpan.Zero);

    private const string Reason = "Bus breakdown at depot";

    // Bus "Alpha" with "Lower berth" and "Upper berth", route Mumbai -> Pune of 210 minutes, schedule
    // S1 at 22:30 IST on Fridays generated through 2026-10-16, and its trip T1 on 2026-10-16, both
    // with "Lower berth" Rs 1,200 and "Upper berth" Rs 1,000 (in paise).
    private const string InsertFleetSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Sleeper', true, NULL, 'Active');
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 5, 2, 1);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Lower berth', 1), (@BusId, 'Upper berth', 2);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'L1', 'Lower', 1, 1, 'Lower berth'), (@BusId, 'U1', 'Lower', 1, 2, 'Upper berth');
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, 'Active');
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Pune, 210);
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '22:30', ARRAY['Friday'], 'Active', '2026-10-16');
        INSERT INTO trips.schedule_class_fares (schedule_id, seat_class, fare)
        VALUES (@ScheduleId, 'Lower berth', 120000), (@ScheduleId, 'Upper berth', 100000);
        INSERT INTO trips.trips (trip_id, schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status,
                                 cancellation_cause, cancel_reason, cancelled_at)
        VALUES (@TripId, @ScheduleId, @OperatorId, @RouteId, @BusId, '2026-10-16', @DepartureAt, @DepartureAt + interval '210 minutes',
                CASE WHEN @Cause IS NULL THEN 'Scheduled' ELSE 'Cancelled' END,
                @Cause,
                CASE WHEN @Cause IS NOT NULL THEN 'Bus sent for repair' END,
                CASE WHEN @Cause IS NOT NULL THEN timestamptz '2026-10-08T10:00:00Z' END);
        INSERT INTO trips.trip_class_fares (trip_id, seat_class, fare)
        VALUES (@TripId, 'Lower berth', 120000), (@TripId, 'Upper berth', 100000);
        """;

    // The trip as "status/cause/reason@cancelled time".
    private const string TripStateSql = """
        SELECT status || '/' || coalesce(cancellation_cause, '-') || '/' || coalesce(cancel_reason, '-') || '@'
               || coalesce(to_char(cancelled_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI'), '-')
        FROM trips.trips WHERE trip_id = @TripId
        """;

    // Events of the trip as "type/tripId-matches/reason/correlation".
    private const string EventSummarySql = """
        SELECT coalesce(string_agg(event_type || '/' || ((data ->> 'tripId')::uuid = trip_id) || '/' || (data ->> 'reason')
                                   || '/' || correlation_id, ','), '-')
        FROM trips.fleet_events WHERE trip_id = @TripId
        """;

    private const string ScheduledDatesSql = """
        SELECT coalesce(string_agg(service_date::text, ',' ORDER BY service_date), '-')
        FROM trips.trips WHERE schedule_id = @ScheduleId AND status = 'Scheduled'
        """;

    private readonly DatabaseFixture _database;

    public CancelTripTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task CancelTrip_ScheduledTripDepartingInTwoHours_ReturnsItCancelledWithReasonAndTime()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);

        // Act
        using HttpResponseMessage response = await CancelAsync(fleet.Token, fleet.TripId, Reason, _now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var trip = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = trip.RootElement;
        Assert.Equal(fleet.TripId, root.GetProperty("tripId").GetGuid());
        Assert.Equal(fleet.ScheduleId, root.GetProperty("scheduleId").GetGuid());
        Assert.Equal("BusCo Travels", root.GetProperty("operatorName").GetString());
        Assert.Equal("Mumbai", root.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal("Pune", root.GetProperty("destination").GetProperty("name").GetString());
        Assert.Equal("Alpha", root.GetProperty("busName").GetString());
        Assert.Equal(_departureAt, root.GetProperty("departureAt").GetDateTimeOffset());
        Assert.Equal("cancelled", root.GetProperty("status").GetString());
        Assert.Equal(
            "[{\"seatClass\":\"Lower berth\",\"fare\":1200},{\"seatClass\":\"Upper berth\",\"fare\":1000}]",
            root.GetProperty("classFares").GetRawText());
        Assert.Equal(Reason, root.GetProperty("cancelReason").GetString());
        Assert.Equal(_now, root.GetProperty("cancelledAt").GetDateTimeOffset());
        Assert.Equal($"Cancelled/Operator/{Reason}@2026-10-16T15:00", await TripStateAsync(fleet.TripId));
    }

    [Fact]
    public async Task CancelTrip_ScheduledTrip_RecordsTripCancelledWithTheTripAndReason()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);

        // Act
        using HttpResponseMessage response = await CancelAsync(fleet.Token, fleet.TripId, Reason, _now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"TripCancelled/true/{Reason}/cancel-test-correlation", await EventSummaryAsync(fleet.TripId));
    }

    [Fact]
    public async Task CancelTrip_ScheduleExtendedAfterwards_GeneratesNoTripForTheCancelledDate()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);
        using HttpResponseMessage cancellation = await CancelAsync(fleet.Token, fleet.TripId, Reason, _now);

        // Act
        using HttpResponseMessage response = await SendAsync(
            fleet.Token, HttpMethod.Post, $"/schedules/{fleet.ScheduleId}/trips", new { EndDate = "2026-10-23" });

        // Assert
        Assert.Equal(HttpStatusCode.OK, cancellation.StatusCode);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("2026-10-23", await ScheduledDatesAsync(fleet.ScheduleId));
    }

    [Fact]
    public async Task CancelTrip_ScheduleEditedAfterwards_GeneratesNoTripForTheCancelledDate()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);
        using HttpResponseMessage cancellation = await CancelAsync(fleet.Token, fleet.TripId, Reason, _now);
        var edit = new
        {
            fleet.RouteId,
            fleet.BusId,
            DepartureTime = "23:00",
            DaysOfWeek = new[] { "friday" },
            ClassFares = new[] { new { SeatClass = "Lower berth", Fare = 1200 }, new { SeatClass = "Upper berth", Fare = 1000 } },
        };

        // Act
        using HttpResponseMessage response = await SendAsync(fleet.Token, HttpMethod.Put, $"/schedules/{fleet.ScheduleId}", edit);

        // Assert
        Assert.Equal(HttpStatusCode.OK, cancellation.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("-", await ScheduledDatesAsync(fleet.ScheduleId));
    }

    [Fact]
    public async Task CancelTrip_AlreadyCancelled_IsRefusedTripNotScheduledAndRecordsNoEvent()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("Operator");

        // Act
        using HttpResponseMessage response = await CancelAsync(fleet.Token, fleet.TripId, Reason, _now);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TRIP_NOT_SCHEDULED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("cancelled", problem.RootElement.GetProperty("currentStatus").GetString());
        Assert.Equal("-", await EventSummaryAsync(fleet.TripId));
        Assert.Equal("Cancelled/Operator/Bus sent for repair@2026-10-08T10:00", await TripStateAsync(fleet.TripId));
    }

    [Theory]
    [InlineData(17, 30)]
    [InlineData(17, 0)]
    public async Task CancelTrip_DepartureTimeReachedOrPassed_IsRefusedTripDeparted(int hourUtc, int minute)
    {
        // Arrange: 23:00 IST, and exactly 22:30 IST.
        Fleet fleet = await SeedFleetAsync(null);
        var now = new DateTimeOffset(2026, 10, 16, hourUtc, minute, 0, TimeSpan.Zero);

        // Act
        using HttpResponseMessage response = await CancelAsync(fleet.Token, fleet.TripId, Reason, now);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TRIP_DEPARTED", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(_departureAt, problem.RootElement.GetProperty("departedAt").GetDateTimeOffset());
        Assert.Equal("Scheduled/-/-@-", await TripStateAsync(fleet.TripId));
    }

    [Fact]
    public async Task CancelTrip_OneMinuteBeforeDeparture_IsAccepted()
    {
        // Arrange: 22:29 IST — there is no cutoff before departure.
        Fleet fleet = await SeedFleetAsync(null);
        var now = new DateTimeOffset(2026, 10, 16, 16, 59, 0, TimeSpan.Zero);

        // Act
        using HttpResponseMessage response = await CancelAsync(fleet.Token, fleet.TripId, Reason, now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(9)]
    [InlineData(501)]
    public async Task CancelTrip_MissingOrBadLengthReason_IsRefusedNamingTheReason(int? length)
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);

        // Act
        using HttpResponseMessage response = await CancelAsync(fleet.Token, fleet.TripId, ReasonOf(length), _now);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("reason", out _), "No error for reason.");
        Assert.Equal("Scheduled/-/-@-", await TripStateAsync(fleet.TripId));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(500)]
    public async Task CancelTrip_ReasonAtTheLengthLimits_IsAccepted(int length)
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);

        // Act
        using HttpResponseMessage response = await CancelAsync(fleet.Token, fleet.TripId, ReasonOf(length), _now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CancelTrip_UnknownTrip_IsRefusedNotFound()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);

        // Act
        using HttpResponseMessage response = await CancelAsync(fleet.Token, Guid.NewGuid(), Reason, _now);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CancelTrip_AnotherOperatorsTrip_IsReportedNotFoundAndLeftScheduled()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(null);
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await CancelAsync(TestAccessTokens.CreateOperator(otherOperator), fleet.TripId, Reason, _now);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal("Scheduled/-/-@-", await TripStateAsync(fleet.TripId));
        Assert.Equal("-", await EventSummaryAsync(fleet.TripId));
    }

    [Fact]
    public async Task CancelTrip_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await CancelAsync(accessToken, Guid.NewGuid(), Reason, _now);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task CancelTrip_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await CancelAsync(accessToken, Guid.NewGuid(), Reason, _now);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CancelTrip_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/trips/{Guid.NewGuid()}/cancel", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static string? ReasonOf(int? length)
    {
        if (length is null)
        {
            return null;
        }

        return new string('x', length.Value);
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
            fleet.TripId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "TC" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            Mumbai = _mumbai,
            Pune = _pune,
            DepartureAt = _departureAt,
            Cause = cause,
        });
        return fleet;
    }

    private async Task<string?> TripStateAsync(Guid tripId)
    {
        return await _database.QueryScalarAsync<string>(TripStateSql, new { TripId = tripId });
    }

    private async Task<string?> EventSummaryAsync(Guid tripId)
    {
        return await _database.QueryScalarAsync<string>(EventSummarySql, new { TripId = tripId });
    }

    private async Task<string?> ScheduledDatesAsync(Guid scheduleId)
    {
        return await _database.QueryScalarAsync<string>(ScheduledDatesSql, new { ScheduleId = scheduleId });
    }

    private async Task<HttpResponseMessage> CancelAsync(string accessToken, Guid tripId, string? reason, DateTimeOffset now)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(now));
        using HttpClient client = factory.CreateClient();
        var body = new { Reason = reason };
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, $"/trips/{tripId}/cancel", accessToken, body);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "cancel-test-correlation");
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(string accessToken, HttpMethod method, string route, object body)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(_now));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(method, route, accessToken, body);
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
}
