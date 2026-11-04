using System.Text.Json;

namespace RouteGo.Fleet.Api.Features.Trips.SearchTrips;

[Collection(DatabaseCollection.Name)]
public sealed class SearchTripsTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _lonavala = Guid.Parse("ef669cf3-be1f-491d-9c86-7395ecb67f75");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    // Friday 2031-01-10, 10:00 IST. The search sees every operator's trips, so these tests use 2031
    // service dates, which no other test seeds, and give each test its own dates.
    private static readonly DateTimeOffset _now = new DateTimeOffset(2031, 1, 10, 4, 30, 0, TimeSpan.Zero);

    // An operator with the given company name, bus "Alpha" ("Lower berth", "Upper berth"), a route
    // through the given three cities and a schedule on them.
    private const string InsertFleetSql = """
        INSERT INTO operator_profiles.operator_profiles (operator_id, company_name, email, rating, approved_at)
        VALUES (@OperatorId, @CompanyName, 'owner@example.in', 4.0, '2026-10-09T09:00:00Z');
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Sleeper', true, NULL, 'Active');
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Lower berth', 1), (@BusId, 'Upper berth', 2);
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, 'Active');
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @FirstCity, 0), (@RouteId, 2, @SecondCity, 90), (@RouteId, 3, @LastCity, 210);
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '22:30', ARRAY['Friday'], 'Active', '2031-12-31');
        """;

    // A trip of the schedule at the given IST time, with "Lower berth" Rs 1,200 and "Upper berth"
    // Rs 1,000 (in paise); a cancelled one carries the cause, reason and time the trips table
    // requires of every cancelled trip.
    private const string InsertTripSql = """
        INSERT INTO trips.trips (trip_id, schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status,
                                 cancellation_cause, cancel_reason, cancelled_at)
        VALUES (@TripId, @ScheduleId, @OperatorId, @RouteId, @BusId, @ServiceDate,
                (@ServiceDate + @DepartureTime) AT TIME ZONE 'Asia/Kolkata',
                (@ServiceDate + @DepartureTime) AT TIME ZONE 'Asia/Kolkata' + interval '210 minutes',
                CASE WHEN @Cause IS NULL THEN 'Scheduled' ELSE 'Cancelled' END,
                @Cause,
                CASE WHEN @Cause IS NOT NULL THEN 'Bus sent for repair' END,
                CASE WHEN @Cause IS NOT NULL THEN timestamptz '2031-01-02T10:00:00Z' END);
        INSERT INTO trips.trip_class_fares (trip_id, seat_class, fare)
        VALUES (@TripId, 'Lower berth', 120000), (@TripId, 'Upper berth', 100000);
        """;

    private readonly DatabaseFixture _database;

    public SearchTripsTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task SearchTrips_BySourceDestinationAndDate_ReturnsEveryOperatorsTripsThatDayByDeparture()
    {
        // Arrange
        Fleet busCo = await SeedFleetAsync("BusCo Travels", _mumbai, _pune);
        Fleet shivneri = await SeedFleetAsync("Shivneri Lines", _mumbai, _pune);
        Fleet returnLeg = await SeedFleetAsync("Shivneri Lines", _pune, _mumbai);
        await SeedTripAsync(busCo, "2031-02-17", "22:30", null);
        await SeedTripAsync(shivneri, "2031-02-17", "21:00", null);
        await SeedTripAsync(returnLeg, "2031-02-17", "20:00", null);
        await SeedTripAsync(busCo, "2031-02-18", "22:30", null);

        // Act
        using JsonDocument page = await SearchPageAsync(
            $"sourceCityId={_mumbai}&destinationCityId={_pune}&from=2031-02-17&to=2031-02-17");

        // Assert
        Assert.Equal("Shivneri Lines,BusCo Travels", Join(page.RootElement, "operatorName"));
        Assert.Equal($"{shivneri.OperatorId},{busCo.OperatorId}", Join(page.RootElement, "operatorId"));
        Assert.Equal(2, page.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task SearchTrips_CancelledTrip_ShowsEveryDetailWithReasonAndTime()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("BusCo Travels", _mumbai, _pune);
        Guid tripId = await SeedTripAsync(fleet, "2031-02-19", "22:30", "Operator");

        // Act
        using JsonDocument page = await SearchPageAsync($"operatorId={fleet.OperatorId}&from=2031-02-19&to=2031-02-19");

        // Assert
        JsonElement trip = page.RootElement.GetProperty("items")[0];
        Assert.Equal(tripId, trip.GetProperty("tripId").GetGuid());
        Assert.Equal(fleet.ScheduleId, trip.GetProperty("scheduleId").GetGuid());
        Assert.Equal(fleet.OperatorId.ToString(), trip.GetProperty("operatorId").GetString());
        Assert.Equal("BusCo Travels", trip.GetProperty("operatorName").GetString());
        Assert.Equal(fleet.RouteId, trip.GetProperty("routeId").GetGuid());
        Assert.Equal("Mumbai", trip.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal(_pune, trip.GetProperty("destination").GetProperty("cityId").GetGuid());
        Assert.Equal(fleet.BusId, trip.GetProperty("busId").GetGuid());
        Assert.Equal("Alpha", trip.GetProperty("busName").GetString());
        Assert.Equal("2031-02-19", trip.GetProperty("serviceDate").GetString());
        Assert.Equal(new DateTimeOffset(2031, 2, 19, 17, 0, 0, TimeSpan.Zero), trip.GetProperty("departureAt").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2031, 2, 19, 20, 30, 0, TimeSpan.Zero), trip.GetProperty("arrivalAt").GetDateTimeOffset());
        Assert.Equal("cancelled", trip.GetProperty("status").GetString());
        Assert.Equal(
            "[{\"seatClass\":\"Lower berth\",\"fare\":1200},{\"seatClass\":\"Upper berth\",\"fare\":1000}]",
            trip.GetProperty("classFares").GetRawText());
        Assert.Equal("Bus sent for repair", trip.GetProperty("cancelReason").GetString());
        Assert.Equal(new DateTimeOffset(2031, 1, 2, 10, 0, 0, TimeSpan.Zero), trip.GetProperty("cancelledAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task SearchTrips_DestinationIsAnIntermediateStop_FindsNothing()
    {
        // Arrange: the route runs Mumbai -> Lonavala -> Pune.
        Fleet fleet = await SeedFleetAsync("BusCo Travels", _mumbai, _pune);
        await SeedTripAsync(fleet, "2031-02-20", "22:30", null);

        // Act
        using JsonDocument page = await SearchPageAsync($"sourceCityId={_mumbai}&destinationCityId={_lonavala}&from=2031-02-20&to=2031-02-20");

        // Assert
        Assert.Equal(0, page.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task SearchTrips_OneOperatorsCancelledTripsInARange_ReturnsOnlyThose()
    {
        // Arrange
        Fleet own = await SeedFleetAsync("BusCo Travels", _mumbai, _pune);
        Fleet other = await SeedFleetAsync("Shivneri Lines", _mumbai, _pune);
        Guid cancelledId = await SeedTripAsync(own, "2031-03-02", "22:30", "Operator");
        await SeedTripAsync(own, "2031-03-03", "22:30", null);
        await SeedTripAsync(own, "2031-03-09", "22:30", "Operator");
        await SeedTripAsync(other, "2031-03-02", "22:30", "Operator");

        // Act
        using JsonDocument page = await SearchPageAsync($"operatorId={own.OperatorId}&status=cancelled&from=2031-03-01&to=2031-03-08");

        // Assert
        Assert.Equal(cancelledId.ToString(), Join(page.RootElement, "tripId"));
    }

    [Theory]
    [InlineData("", "departed,cancelled,scheduled")]
    [InlineData("&status=departed", "departed")]
    [InlineData("&status=scheduled", "scheduled")]
    public async Task SearchTrips_DepartedScheduledAndCancelledTrips_ReportsAndFiltersEachStatus(string statusQuery, string expected)
    {
        // Arrange: at 10:00 IST on 2031-01-10 yesterday's trip has departed and today's 22:30 one has not.
        Fleet fleet = await SeedFleetAsync("BusCo Travels", _mumbai, _pune);
        await SeedTripAsync(fleet, "2031-01-09", "22:30", null);
        await SeedTripAsync(fleet, "2031-01-10", "21:00", "Operator");
        await SeedTripAsync(fleet, "2031-01-10", "22:30", null);

        // Act
        using JsonDocument page = await SearchPageAsync($"operatorId={fleet.OperatorId}&from=2031-01-09&to=2031-01-10" + statusQuery);

        // Assert
        Assert.Equal(expected, Join(page.RootElement, "status"));
    }

    [Fact]
    public async Task SearchTrips_WithoutDates_ReturnsTodayThroughThirteenDaysAhead()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("BusCo Travels", _mumbai, _pune);
        await SeedTripsAsync(fleet, "2031-01-09", "2031-01-10", "2031-01-23", "2031-01-24");

        // Act
        using JsonDocument page = await SearchPageAsync($"operatorId={fleet.OperatorId}");

        // Assert
        Assert.Equal("2031-01-10,2031-01-23", Join(page.RootElement, "serviceDate"));
    }

    [Theory]
    [InlineData("&from=2031-01-15", "2031-01-15,2031-01-28")]
    [InlineData("&to=2031-01-28", "2031-01-15,2031-01-28")]
    public async Task SearchTrips_OnlyOneEndGiven_CoversFourteenDaysFromThatEnd(string rangeQuery, string expected)
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("BusCo Travels", _mumbai, _pune);
        await SeedTripsAsync(fleet, "2031-01-14", "2031-01-15", "2031-01-28", "2031-01-29");

        // Act
        using JsonDocument page = await SearchPageAsync($"operatorId={fleet.OperatorId}" + rangeQuery);

        // Assert
        Assert.Equal(expected, Join(page.RootElement, "serviceDate"));
    }

    [Fact]
    public async Task SearchTrips_RouteAndDateWithNoTrips_ReturnsAnEmptyPage()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync("BusCo Travels", _mumbai, _pune);
        await SeedTripAsync(fleet, "2031-04-01", "22:30", null);

        // Act
        using JsonDocument page = await SearchPageAsync($"sourceCityId={_pune}&destinationCityId={_mumbai}&from=2031-04-01&to=2031-04-01");

        // Assert
        Assert.Equal(0, page.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(0, page.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("from=2031-01-20&to=2031-01-10", "to")]
    [InlineData("from=2031-01-01&to=2031-02-01", "to")]
    [InlineData("from=2031-02-30", "from")]
    [InlineData("operatorId=OP-1", "operatorId")]
    [InlineData("sourceCityId=Mumbai", "sourceCityId")]
    [InlineData("destinationCityId=Pune", "destinationCityId")]
    [InlineData("status=Cancelled", "status")]
    [InlineData("pageSize=0", "pageSize")]
    public async Task SearchTrips_InvalidRangeOrFilter_IsRefusedNamingTheField(string query, string field)
    {
        // Act
        using HttpResponseMessage response = await SearchAsync(TestAccessTokens.Create("Admin"), query);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"No error for {field}.");
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Customer")]
    public async Task SearchTrips_NotAnAdmin_IsRefusedForbidden(string role)
    {
        // Act
        using HttpResponseMessage response = await SearchAsync(TestAccessTokens.Create(role), string.Empty);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task SearchTrips_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/admin/trips");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static string Join(JsonElement page, string property)
    {
        IEnumerable<string?> values = page.GetProperty("items").EnumerateArray().Select(trip => trip.GetProperty(property).GetString());
        return string.Join(",", values);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Fleet> SeedFleetAsync(string companyName, Guid firstCity, Guid lastCity)
    {
        var fleet = new Fleet { OperatorId = Guid.NewGuid(), BusId = Guid.NewGuid(), RouteId = Guid.NewGuid(), ScheduleId = Guid.NewGuid() };
        await _database.SeedAsync(InsertFleetSql, new
        {
            OperatorId = fleet.OperatorId.ToString(),
            CompanyName = companyName,
            fleet.BusId,
            fleet.RouteId,
            fleet.ScheduleId,
            RegistrationNumber = "TA" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            FirstCity = firstCity,
            SecondCity = _lonavala,
            LastCity = lastCity,
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

    private async Task<JsonDocument> SearchPageAsync(string query)
    {
        using HttpResponseMessage response = await SearchAsync(TestAccessTokens.Create("Admin"), query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SearchAsync(string accessToken, string query)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(_now));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Get, $"/admin/trips?{query}", accessToken);
        return await client.SendAsync(request);
    }

    /// <summary>An operator with a profile, its bus, its three-stop route and a schedule on them.</summary>
    private sealed class Fleet
    {
        public required Guid OperatorId { get; init; }

        public required Guid BusId { get; init; }

        public required Guid RouteId { get; init; }

        public required Guid ScheduleId { get; init; }
    }
}
