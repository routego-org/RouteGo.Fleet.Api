using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Trips.ListTrips;

[Collection(DatabaseCollection.Name)]
public sealed class ListTripsTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _lonavala = Guid.Parse("ef669cf3-be1f-491d-9c86-7395ecb67f75");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    // Friday 2026-10-09, 23:30 IST.
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero);

    // The bus lists "Upper berth" before "Lower berth", so fares in that order prove the bus's order is kept.
    private const string InsertFleetSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, @BusName, 'Sleeper', true, NULL, 'Active');
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Upper berth', 1), (@BusId, 'Lower berth', 2);
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, 'Active');
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Lonavala, 90), (@RouteId, 3, @Pune, 210);
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '22:30', ARRAY['Friday'], 'Active', '2026-11-06');
        """;

    // A trip of the schedule departing at the given IST time, with "Lower berth" Rs 1,200 and
    // "Upper berth" Rs 1,000 (in paise); a cancelled one carries the cause, reason and time the
    // trips table requires of every cancelled trip.
    private const string InsertTripSql = """
        INSERT INTO trips.trips (trip_id, schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status,
                                 cancellation_cause, cancel_reason, cancelled_at)
        VALUES (@TripId, @ScheduleId, @OperatorId, @RouteId, @BusId, @ServiceDate,
                (@ServiceDate + @DepartureTime) AT TIME ZONE 'Asia/Kolkata',
                (@ServiceDate + @DepartureTime) AT TIME ZONE 'Asia/Kolkata' + interval '210 minutes',
                CASE WHEN @Cause IS NULL THEN 'Scheduled' ELSE 'Cancelled' END,
                @Cause,
                CASE WHEN @Cause IS NOT NULL THEN 'Bus sent for repair' END,
                CASE WHEN @Cause IS NOT NULL THEN timestamptz '2026-10-08T10:00:00Z' END);
        INSERT INTO trips.trip_class_fares (trip_id, seat_class, fare)
        VALUES (@TripId, 'Lower berth', 120000), (@TripId, 'Upper berth', 100000);
        """;

    private readonly DatabaseFixture _database;

    public ListTripsTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ListTrips_WithoutDates_ReturnsTodayThroughThirteenDaysAheadByDeparture()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("Alpha");
        await SeedTripsAsync(fleet, "2026-10-01", "2026-10-08", "2026-10-22", "2026-10-09", "2026-10-15", "2026-10-23", "2026-11-06");

        // Act
        using JsonDocument page = await ListPageAsync(fleet.Token, string.Empty);

        // Assert
        Assert.Equal("2026-10-09,2026-10-15,2026-10-22", ServiceDates(page.RootElement));
        Assert.Equal(3, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(20, page.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task ListTrips_ScheduledTrip_ShowsEveryDetail()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("Alpha");
        Guid tripId = await SeedTripAsync(fleet, "2026-10-15", "22:30", null);

        // Act
        using JsonDocument page = await ListPageAsync(fleet.Token, string.Empty);

        // Assert
        JsonElement trip = page.RootElement.GetProperty("items")[0];
        Assert.Equal(tripId, trip.GetProperty("tripId").GetGuid());
        Assert.Equal(fleet.ScheduleId, trip.GetProperty("scheduleId").GetGuid());
        Assert.Equal(fleet.OperatorId.ToString(), trip.GetProperty("operatorId").GetString());
        Assert.Equal("BusCo Travels", trip.GetProperty("operatorName").GetString());
        Assert.Equal(fleet.RouteId, trip.GetProperty("routeId").GetGuid());
        Assert.Equal("{\"cityId\":\"ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37\",\"name\":\"Mumbai\"}", trip.GetProperty("source").GetRawText());
        Assert.Equal("{\"cityId\":\"0484575c-a368-4057-896d-c99d78ba9cce\",\"name\":\"Pune\"}", trip.GetProperty("destination").GetRawText());
        Assert.Equal(fleet.BusId, trip.GetProperty("busId").GetGuid());
        Assert.Equal("Alpha", trip.GetProperty("busName").GetString());
        Assert.Equal("2026-10-15", trip.GetProperty("serviceDate").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 17, 0, 0, TimeSpan.Zero), trip.GetProperty("departureAt").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 20, 30, 0, TimeSpan.Zero), trip.GetProperty("arrivalAt").GetDateTimeOffset());
        Assert.Equal("scheduled", trip.GetProperty("status").GetString());
        Assert.Equal(
            "[{\"seatClass\":\"Upper berth\",\"fare\":1000},{\"seatClass\":\"Lower berth\",\"fare\":1200}]",
            trip.GetProperty("classFares").GetRawText());
        Assert.Equal(JsonValueKind.Null, trip.GetProperty("cancelReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, trip.GetProperty("cancelledAt").ValueKind);
    }

    [Fact]
    public async Task ListTrips_CancelledTrip_ShowsItsReasonAndCancellationTime()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("Alpha");
        await SeedTripAsync(fleet, "2026-10-15", "22:30", "Operator");

        // Act
        using JsonDocument page = await ListPageAsync(fleet.Token, string.Empty);

        // Assert
        JsonElement trip = page.RootElement.GetProperty("items")[0];
        Assert.Equal("cancelled", trip.GetProperty("status").GetString());
        Assert.Equal("Bus sent for repair", trip.GetProperty("cancelReason").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero), trip.GetProperty("cancelledAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("", "departed,cancelled,scheduled")]
    [InlineData("&status=departed", "departed")]
    [InlineData("&status=scheduled", "scheduled")]
    [InlineData("&status=cancelled", "cancelled")]
    public async Task ListTrips_DepartedScheduledAndCancelledTrips_ReportsAndFiltersEachStatus(string statusQuery, string expected)
    {
        // Arrange: T1 departed at 22:30 IST today, T3 was cancelled, T2 departs tomorrow at 22:30.
        Fleet fleet = await SeedFleetAsync("Alpha");
        await SeedTripAsync(fleet, "2026-10-09", "22:30", null);
        await SeedTripAsync(fleet, "2026-10-10", "21:00", "Operator");
        await SeedTripAsync(fleet, "2026-10-10", "22:30", null);

        // Act
        using JsonDocument page = await ListPageAsync(fleet.Token, "from=2026-10-09&to=2026-10-10" + statusQuery);

        // Assert
        Assert.Equal(expected, Statuses(page.RootElement));
    }

    [Fact]
    public async Task ListTrips_FilteredByScheduleOrBus_ReturnsOnlyThatSchedulesOrBussTrips()
    {
        // Arrange: S1 runs on "Alpha", S2 on "Beta", both the same operator's.
        Fleet alpha = await SeedFleetAsync("Alpha");
        Fleet beta = await SeedFleetForAsync(alpha.OperatorId, "Beta");
        await SeedTripsAsync(alpha, "2026-10-12", "2026-10-13");
        await SeedTripsAsync(beta, "2026-10-14");

        // Act
        using JsonDocument bySchedule = await ListPageAsync(alpha.Token, $"scheduleId={alpha.ScheduleId}");
        using JsonDocument byBus = await ListPageAsync(alpha.Token, $"busId={beta.BusId}");

        // Assert
        Assert.Equal("2026-10-12,2026-10-13", ServiceDates(bySchedule.RootElement));
        Assert.Equal("2026-10-14", ServiceDates(byBus.RootElement));
    }

    [Fact]
    public async Task ListTrips_AnotherOperatorHasTripsInTheRange_ReturnsOnlyOwnTrips()
    {
        // Arrange
        Fleet own = await SeedFleetAsync("Alpha");
        Fleet other = await SeedFleetAsync("Alpha");
        await SeedTripsAsync(own, "2026-10-12");
        await SeedTripsAsync(other, "2026-10-12", "2026-10-13");

        // Act
        using JsonDocument page = await ListPageAsync(own.Token, string.Empty);

        // Assert
        Assert.Equal(1, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(own.ScheduleId, page.RootElement.GetProperty("items")[0].GetProperty("scheduleId").GetGuid());
    }

    [Fact]
    public async Task ListTrips_RangeWithNoTrips_ReturnsAnEmptyPage()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("Alpha");
        await SeedTripsAsync(fleet, "2026-10-12");

        // Act
        using JsonDocument page = await ListPageAsync(fleet.Token, "from=2026-12-01&to=2026-12-05");

        // Assert
        Assert.Equal(0, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(0, page.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("from=2026-10-20&to=2026-10-10", "to")]
    [InlineData("from=2026-10-01&to=2026-11-05", "to")]
    [InlineData("from=2026-13-01", "from")]
    [InlineData("to=10/22/2026", "to")]
    [InlineData("scheduleId=S1", "scheduleId")]
    [InlineData("busId=alpha", "busId")]
    [InlineData("status=Scheduled", "status")]
    [InlineData("page=0", "page")]
    [InlineData("pageSize=201", "pageSize")]
    public async Task ListTrips_InvalidRangeOrFilter_IsRefusedNamingTheField(string query, string field)
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("Alpha");

        // Act
        using HttpResponseMessage response = await ListAsync(fleet.Token, query);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"No error for {field}.");
    }

    [Fact]
    public async Task ListTrips_RangeOfExactlyThirtyOneDays_IsAccepted()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("Alpha");
        await SeedTripsAsync(fleet, "2026-10-01", "2026-10-31", "2026-11-01");

        // Act
        using JsonDocument page = await ListPageAsync(fleet.Token, "from=2026-10-01&to=2026-10-31");

        // Assert
        Assert.Equal("2026-10-01,2026-10-31", ServiceDates(page.RootElement));
    }

    [Theory]
    [InlineData("from=2026-10-15", "2026-10-15,2026-10-28")]
    [InlineData("to=2026-10-28", "2026-10-15,2026-10-28")]
    public async Task ListTrips_OnlyOneEndGiven_CoversFourteenDaysFromThatEnd(string query, string expected)
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("Alpha");
        await SeedTripsAsync(fleet, "2026-10-14", "2026-10-15", "2026-10-28", "2026-10-29");

        // Act
        using JsonDocument page = await ListPageAsync(fleet.Token, query);

        // Assert
        Assert.Equal(expected, ServiceDates(page.RootElement));
    }

    [Fact]
    public async Task ListTrips_SecondPageOfOne_ReturnsTheSecondTripByDeparture()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("Alpha");
        await SeedTripsAsync(fleet, "2026-10-14", "2026-10-12", "2026-10-13");

        // Act
        using JsonDocument page = await ListPageAsync(fleet.Token, "page=2&pageSize=1");

        // Assert
        Assert.Equal("2026-10-13", ServiceDates(page.RootElement));
        Assert.Equal(3, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, page.RootElement.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task ListTrips_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await ListAsync(accessToken, string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task ListTrips_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await ListAsync(accessToken, string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ListTrips_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/trips");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static string ServiceDates(JsonElement page)
    {
        IEnumerable<string?> dates = page.GetProperty("items").EnumerateArray().Select(trip => trip.GetProperty("serviceDate").GetString());
        return string.Join(",", dates);
    }

    private static string Statuses(JsonElement page)
    {
        IEnumerable<string?> statuses = page.GetProperty("items").EnumerateArray().Select(trip => trip.GetProperty("status").GetString());
        return string.Join(",", statuses);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Fleet> SeedFleetAsync(string busName)
    {
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        return await SeedFleetForAsync(operatorId, busName);
    }

    private async Task<Fleet> SeedFleetForAsync(Guid operatorId, string busName)
    {
        var fleet = new Fleet { OperatorId = operatorId, BusId = Guid.NewGuid(), RouteId = Guid.NewGuid(), ScheduleId = Guid.NewGuid() };
        await _database.SeedAsync(InsertFleetSql, new
        {
            fleet.BusId,
            fleet.RouteId,
            fleet.ScheduleId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "TL" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            BusName = busName,
            Mumbai = _mumbai,
            Lonavala = _lonavala,
            Pune = _pune,
        });
        return fleet;
    }

    private async Task SeedTripsAsync(Fleet fleet, params string[] serviceDates)
    {
        foreach (string serviceDate in serviceDates)
        {
            await SeedTripAsync(fleet, serviceDate, "22:30", null);
        }
    }

    private async Task<Guid> SeedTripAsync(Fleet fleet, string serviceDate, string departureTime, string? cause)
    {
        var tripId = Guid.NewGuid();
        await _database.SeedAsync(InsertTripSql, new
        {
            TripId = tripId,
            fleet.ScheduleId,
            OperatorId = fleet.OperatorId.ToString(),
            fleet.RouteId,
            fleet.BusId,
            ServiceDate = DateOnly.Parse(serviceDate, System.Globalization.CultureInfo.InvariantCulture),
            DepartureTime = TimeOnly.Parse(departureTime, System.Globalization.CultureInfo.InvariantCulture),
            Cause = cause,
        });
        return tripId;
    }

    private async Task<JsonDocument> ListPageAsync(string accessToken, string query)
    {
        using HttpResponseMessage response = await ListAsync(accessToken, query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> ListAsync(string accessToken, string query)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(_now));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, $"/trips?{query}", accessToken);
        return await client.SendAsync(request);
    }

    /// <summary>An operator with a profile, its bus, its Mumbai -> Lonavala -> Pune route and a schedule on them.</summary>
    private sealed class Fleet
    {
        public required Guid OperatorId { get; init; }

        public required Guid BusId { get; init; }

        public required Guid RouteId { get; init; }

        public required Guid ScheduleId { get; init; }

        public string Token
        {
            get { return TestAccessTokens.CreateOperator(OperatorId); }
        }
    }
}
