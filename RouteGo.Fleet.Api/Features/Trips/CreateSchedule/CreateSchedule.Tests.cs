using System.Net.Http.Json;
using System.Text.Json;
using RouteGo.Fleet.Api.Common.Behaviors;

namespace RouteGo.Fleet.Api.Features.Trips.CreateSchedule;

[Collection(DatabaseCollection.Name)]
public sealed class CreateScheduleTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _lonavala = Guid.Parse("ef669cf3-be1f-491d-9c86-7395ecb67f75");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    // Friday 2026-10-09, 10:00 IST and 23:00 IST.
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 9, 4, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _lateEvening = new DateTimeOffset(2026, 10, 9, 17, 30, 0, TimeSpan.Zero);

    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Sleeper', true, ARRAY['Wifi', 'Blanket'], @Status);
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 5, 2, 1), (@BusId, 'Upper', 5, 2, 1);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Lower berth', 1), (@BusId, 'Upper berth', 2);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'L1', 'Lower', 1, 1, 'Lower berth'), (@BusId, 'U1', 'Upper', 1, 1, 'Upper berth');
        """;

    // Mumbai -> Lonavala -> Pune, Lonavala reached halfway through the journey.
    private const string InsertRouteSql = """
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, @Status);
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Lonavala, @MidwayMinutes), (@RouteId, 3, @Pune, @JourneyMinutes);
        """;

    // A trip of another schedule on the same bus. A cancelled one carries the cause, reason and
    // time the trips table requires of every cancelled trip.
    private const string InsertOtherTripSql = """
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '18:00', ARRAY['Monday'], 'Active', '2026-10-22');
        INSERT INTO trips.trips (trip_id, schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status,
                                 cancellation_cause, cancel_reason, cancelled_at)
        VALUES (@TripId, @ScheduleId, @OperatorId, @RouteId, @BusId, (@DepartureAt AT TIME ZONE 'Asia/Kolkata')::date,
                @DepartureAt, @ArrivalAt, @Status,
                CASE WHEN @Status = 'Cancelled' THEN 'Operator' END,
                CASE WHEN @Status = 'Cancelled' THEN 'Bus sent for repair' END,
                CASE WHEN @Status = 'Cancelled' THEN @DepartureAt - interval '1 day' END);
        """;

    private const string UpdateBusDetailsSql = """
        UPDATE buses.buses SET name = 'Shivneri Sleeper', is_ac = false, amenities = ARRAY['ChargingPoint'] WHERE bus_id = @BusId
        """;

    private const string ServiceDatesSql = """
        SELECT string_agg(service_date::text, ',' ORDER BY service_date) FROM trips.trips WHERE schedule_id = @ScheduleId
        """;

    private const string DepartureSql = """
        SELECT departure_at FROM trips.trips WHERE schedule_id = @ScheduleId AND service_date = '2026-10-09'
        """;

    private const string ArrivalSql = """
        SELECT arrival_at FROM trips.trips WHERE schedule_id = @ScheduleId AND service_date = '2026-10-09'
        """;

    private const string TripFaresSql = """
        SELECT string_agg(f.seat_class || '=' || f.fare, ',' ORDER BY f.seat_class)
        FROM trips.trip_class_fares f JOIN trips.trips t ON t.trip_id = f.trip_id
        WHERE t.schedule_id = @ScheduleId AND t.service_date = '2026-10-09'
        """;

    private const string ScheduleFaresSql = """
        SELECT string_agg(seat_class || '=' || fare, ',' ORDER BY seat_class) FROM trips.schedule_class_fares WHERE schedule_id = @ScheduleId
        """;

    private const string EventSummarySql = """
        SELECT count(*) || ' ' || string_agg(DISTINCT e.event_type || '/' || e.correlation_id, ',')
        FROM trips.fleet_events e JOIN trips.trips t ON t.trip_id = e.trip_id
        WHERE t.schedule_id = @ScheduleId
        """;

    private const string EventDataSql = """
        SELECT e.data::text
        FROM trips.fleet_events e JOIN trips.trips t ON t.trip_id = e.trip_id
        WHERE t.schedule_id = @ScheduleId AND t.service_date = @ServiceDate::date
        """;

    private const string BusSchedulesSql = """
        SELECT count(*) || ' schedules, ' || (SELECT count(*) FROM trips.trips WHERE bus_id = @BusId) || ' trips'
        FROM trips.schedules WHERE bus_id = @BusId
        """;

    private readonly DatabaseFixture _database;

    public CreateScheduleTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task CreateSchedule_MondayWednesdayFriday_IsCreatedActiveThroughTwoWeeks()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(fleet.Token, Body(fleet), _now);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var schedule = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = schedule.RootElement;
        Assert.Equal($"/schedules/{root.GetProperty("scheduleId").GetGuid()}", response.Headers.Location!.OriginalString);
        Assert.Equal(fleet.OperatorId.ToString(), root.GetProperty("operatorId").GetString());
        Assert.Equal("22:30", root.GetProperty("departureTime").GetString());
        Assert.Equal("[\"monday\",\"wednesday\",\"friday\"]", root.GetProperty("daysOfWeek").GetRawText());
        Assert.Equal("active", root.GetProperty("status").GetString());
        Assert.Equal("2026-10-22", root.GetProperty("tripsGeneratedThrough").GetString());
        Assert.Equal(
            "[{\"seatClass\":\"Lower berth\",\"fare\":1200},{\"seatClass\":\"Upper berth\",\"fare\":1000}]",
            root.GetProperty("classFares").GetRawText());
    }

    [Fact]
    public async Task CreateSchedule_MondayWednesdayFriday_GeneratesSixTripsWithTheFares()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();

        // Act
        Guid scheduleId = await CreateAndReadIdAsync(fleet, Body(fleet), _now);

        // Assert
        var lookup = new { ScheduleId = scheduleId };
        Assert.Equal("2026-10-09,2026-10-12,2026-10-14,2026-10-16,2026-10-19,2026-10-21",
            await _database.QueryScalarAsync<string>(ServiceDatesSql, lookup));
        Assert.Equal(new DateTime(2026, 10, 9, 17, 0, 0), await _database.QueryScalarAsync<DateTime>(DepartureSql, lookup));
        Assert.Equal(new DateTime(2026, 10, 9, 20, 30, 0), await _database.QueryScalarAsync<DateTime>(ArrivalSql, lookup));
        Assert.Equal("Lower berth=120000,Upper berth=100000", await _database.QueryScalarAsync<string>(TripFaresSql, lookup));
        Assert.Equal("Lower berth=120000,Upper berth=100000", await _database.QueryScalarAsync<string>(ScheduleFaresSql, lookup));
    }

    [Fact]
    public async Task CreateSchedule_MondayWednesdayFriday_RecordsTripScheduledForEveryTripWithOperatorAndBus()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();

        // Act
        Guid scheduleId = await CreateAndReadIdAsync(fleet, Body(fleet), _now);

        // Assert
        Assert.Equal("6 TripScheduled/schedule-test-correlation",
            await _database.QueryScalarAsync<string>(EventSummarySql, new { ScheduleId = scheduleId }));
        using JsonDocument snapshot = await ReadEventDataAsync(scheduleId, "2026-10-09");
        JsonElement data = snapshot.RootElement;
        Assert.Equal(scheduleId, data.GetProperty("scheduleId").GetGuid());
        Assert.Equal(fleet.OperatorId.ToString(), data.GetProperty("operatorId").GetString());
        Assert.Equal("BusCo Travels", data.GetProperty("operatorName").GetString());
        Assert.Equal("owner@busco.in", data.GetProperty("operatorEmail").GetString());
        Assert.Equal(4.0m, data.GetProperty("rating").GetDecimal());
        Assert.Equal(fleet.BusId, data.GetProperty("busId").GetGuid());
        Assert.Equal("Alpha", data.GetProperty("busName").GetString());
        Assert.Equal("Sleeper", data.GetProperty("busType").GetString());
        Assert.True(data.GetProperty("isAc").GetBoolean());
        Assert.Equal("Wifi,Blanket", Join(data.GetProperty("amenities")));
    }

    [Fact]
    public async Task CreateSchedule_MondayWednesdayFriday_SnapshotCarriesTheRouteAndTimes()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();

        // Act
        Guid scheduleId = await CreateAndReadIdAsync(fleet, Body(fleet), _now);

        // Assert
        using JsonDocument snapshot = await ReadEventDataAsync(scheduleId, "2026-10-09");
        JsonElement data = snapshot.RootElement;
        Assert.Equal(_mumbai, data.GetProperty("source").GetProperty("cityId").GetGuid());
        Assert.Equal("Mumbai", data.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal("Pune", data.GetProperty("destination").GetProperty("name").GetString());
        Assert.Equal("2026-10-09", data.GetProperty("serviceDate").GetString());
        Assert.Equal("2026-10-09T17:00:00Z", data.GetProperty("departureUtc").GetString());
        Assert.Equal("2026-10-09T20:30:00Z", data.GetProperty("arrivalUtc").GetString());
        JsonElement midway = data.GetProperty("stops")[1];
        Assert.Equal(3, data.GetProperty("stops").GetArrayLength());
        Assert.Equal(2, midway.GetProperty("sequence").GetInt32());
        Assert.Equal(_lonavala, midway.GetProperty("cityId").GetGuid());
        Assert.Equal("Lonavala", midway.GetProperty("cityName").GetString());
        Assert.Equal("2026-10-09T18:45:00Z", midway.GetProperty("arrivalUtc").GetString());
    }

    [Fact]
    public async Task CreateSchedule_MondayWednesdayFriday_SnapshotPricesEverySeat()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();

        // Act
        Guid scheduleId = await CreateAndReadIdAsync(fleet, Body(fleet), _now);

        // Assert
        using JsonDocument snapshot = await ReadEventDataAsync(scheduleId, "2026-10-09");
        JsonElement data = snapshot.RootElement;
        JsonElement upperSeat = data.GetProperty("seats")[1];
        Assert.Equal(2, data.GetProperty("seats").GetArrayLength());
        Assert.Equal("U1", upperSeat.GetProperty("seatNo").GetString());
        Assert.Equal("Upper", upperSeat.GetProperty("deck").GetString());
        Assert.Equal(1, upperSeat.GetProperty("row").GetInt32());
        Assert.Equal(1, upperSeat.GetProperty("col").GetInt32());
        Assert.Equal("Upper berth", upperSeat.GetProperty("class").GetString());
        Assert.Equal(1000, upperSeat.GetProperty("fare").GetInt32());
        Assert.Equal(1200, data.GetProperty("seats")[0].GetProperty("fare").GetInt32());
        Assert.Equal(2, data.GetProperty("totalSeats").GetInt32());
    }

    [Fact]
    public async Task CreateSchedule_TodaysDepartureHasPassed_GeneratesFromTheNextDayOfService()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();

        // Act
        Guid scheduleId = await CreateAndReadIdAsync(fleet, Body(fleet), _lateEvening);

        // Assert
        Assert.Equal("2026-10-12,2026-10-14,2026-10-16,2026-10-19,2026-10-21",
            await _database.QueryScalarAsync<string>(ServiceDatesSql, new { ScheduleId = scheduleId }));
    }

    [Fact]
    public async Task CreateSchedule_FareClassInAnotherCase_IsKeptInTheBusSpelling()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();
        ScheduleBody body = Body(fleet) with
        {
            ClassFares = new[] { new FareBody("  UPPER BERTH ", 1000), new FareBody("lower berth", 1200) },
        };

        // Act
        Guid scheduleId = await CreateAndReadIdAsync(fleet, body, _now);

        // Assert
        Assert.Equal("Lower berth=120000,Upper berth=100000",
            await _database.QueryScalarAsync<string>(ScheduleFaresSql, new { ScheduleId = scheduleId }));
    }

    [Theory]
    [InlineData("Lower berth", null, "[\"Upper berth\"]", "[]")]
    [InlineData("Lower berth", "Window", "[]", "[\"Window\"]")]
    public async Task CreateSchedule_FaresNotNamingExactlyTheSeatClasses_IsRefusedClassFaresMismatch(
        string firstClass, string? extraClass, string missingClasses, string unknownClasses)
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();
        ScheduleBody body = Body(fleet) with { ClassFares = MismatchedFares(firstClass, extraClass) };

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(fleet.Token, body, _now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CLASS_FARES_MISMATCH", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(missingClasses, problem.RootElement.GetProperty("missingClasses").GetRawText());
        Assert.Equal(unknownClasses, problem.RootElement.GetProperty("unknownClasses").GetRawText());
        Assert.Equal("0 schedules, 0 trips", await BusSchedulesAsync(fleet.BusId));
    }

    [Theory]
    [InlineData("departure 24:00", "departureTime")]
    [InlineData("no day", "daysOfWeek")]
    [InlineData("monday twice", "daysOfWeek")]
    [InlineData("fare 49", "classFares[0].fare")]
    [InlineData("fare 10001", "classFares[0].fare")]
    [InlineData("fare 999.50", "classFares[0].fare")]
    [InlineData("lower berth twice", "classFares[1].seatClass")]
    public async Task CreateSchedule_FieldBreaksARule_IsRefusedNamingTheField(string rule, string field)
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(fleet.Token, Breaking(rule, Body(fleet)), _now);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"No error for {field}.");
        Assert.Equal("0 schedules, 0 trips", await BusSchedulesAsync(fleet.BusId));
    }

    [Fact]
    public async Task CreateSchedule_UnknownBus_IsRefusedUnknownBus()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();
        var unknownBusId = Guid.NewGuid();

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(fleet.Token, Body(fleet) with { BusId = unknownBusId }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UNKNOWN_BUS", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(unknownBusId, problem.RootElement.GetProperty("busId").GetGuid());
    }

    [Fact]
    public async Task CreateSchedule_AnotherOperatorsBus_IsRefusedUnknownBus()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();
        Fleet otherFleet = await SeedFleetAsync();

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(
            fleet.Token, Body(fleet) with { BusId = otherFleet.BusId }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("UNKNOWN_BUS", await ProblemCodeAsync(response));
        Assert.Equal("0 schedules, 0 trips", await BusSchedulesAsync(otherFleet.BusId));
    }

    [Fact]
    public async Task CreateSchedule_UnknownRoute_IsRefusedUnknownRoute()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();
        var unknownRouteId = Guid.NewGuid();

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(fleet.Token, Body(fleet) with { RouteId = unknownRouteId }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("UNKNOWN_ROUTE", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(unknownRouteId, problem.RootElement.GetProperty("routeId").GetGuid());
    }

    [Fact]
    public async Task CreateSchedule_AnotherOperatorsRoute_IsRefusedUnknownRoute()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();
        Fleet otherFleet = await SeedFleetAsync();

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(
            fleet.Token, Body(fleet) with { RouteId = otherFleet.RouteId }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("UNKNOWN_ROUTE", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CreateSchedule_InactiveBus_IsRefusedBusInactive()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(busStatus: "Inactive");

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(fleet.Token, Body(fleet), _now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BUS_INACTIVE", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(fleet.BusId, problem.RootElement.GetProperty("busId").GetGuid());
    }

    [Fact]
    public async Task CreateSchedule_InactiveRoute_IsRefusedRouteInactive()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(routeStatus: "Inactive");

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(fleet.Token, Body(fleet), _now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ROUTE_INACTIVE", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(fleet.RouteId, problem.RootElement.GetProperty("routeId").GetGuid());
        Assert.Equal("0 schedules, 0 trips", await BusSchedulesAsync(fleet.BusId));
    }

    [Fact]
    public async Task CreateSchedule_UnknownRouteAndInactiveBus_IsRefusedForTheRouteFirst()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(busStatus: "Inactive");

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(fleet.Token, Body(fleet) with { RouteId = Guid.NewGuid() }, _now);

        // Assert
        Assert.Equal("UNKNOWN_ROUTE", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CreateSchedule_BusOnAnotherTripAtTheTime_IsRefusedBusTripOverlapAndNothingChanges()
    {
        // Arrange: 21:00 IST Monday 2026-10-12, arriving 00:30 IST.
        Fleet fleet = await SeedFleetAsync();
        var departureAt = new DateTimeOffset(2026, 10, 12, 15, 30, 0, TimeSpan.Zero);
        Guid otherTripId = await SeedOtherTripAsync(fleet, departureAt, departureAt.AddMinutes(210), "Scheduled");

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(fleet.Token, Body(fleet), _now);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BUS_TRIP_OVERLAP", problem.RootElement.GetProperty("code").GetString());
        JsonElement conflicts = problem.RootElement.GetProperty("conflicts");
        Assert.Equal(1, conflicts.GetArrayLength());
        Assert.Equal("2026-10-12", conflicts[0].GetProperty("serviceDate").GetString());
        Assert.Equal(otherTripId, conflicts[0].GetProperty("tripId").GetGuid());
        Assert.Equal(departureAt, conflicts[0].GetProperty("departureAt").GetDateTimeOffset());
        Assert.Equal("1 schedules, 1 trips", await BusSchedulesAsync(fleet.BusId));
    }

    [Fact]
    public async Task CreateSchedule_BusArrivesWithinTheTurnaroundBuffer_IsRefusedBusTripOverlap()
    {
        // Arrange: arrives 21:45 IST on 2026-10-12, so the bus is busy until 22:45 IST.
        Fleet fleet = await SeedFleetAsync();
        var arrivalAt = new DateTimeOffset(2026, 10, 12, 16, 15, 0, TimeSpan.Zero);
        await SeedOtherTripAsync(fleet, arrivalAt.AddMinutes(-210), arrivalAt, "Scheduled");

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(
            fleet.Token, Body(fleet) with { DaysOfWeek = new[] { "monday" } }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("BUS_TRIP_OVERLAP", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CreateSchedule_BusFreeExactlyAtDeparture_IsCreated()
    {
        // Arrange: arrives 21:30 IST on 2026-10-12, free from 22:30 IST.
        Fleet fleet = await SeedFleetAsync();
        var arrivalAt = new DateTimeOffset(2026, 10, 12, 16, 0, 0, TimeSpan.Zero);
        await SeedOtherTripAsync(fleet, arrivalAt.AddMinutes(-210), arrivalAt, "Scheduled");

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(
            fleet.Token, Body(fleet) with { DaysOfWeek = new[] { "monday" } }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateSchedule_CancelledTripAtTheTime_DoesNotBlockTheBus()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();
        var departureAt = new DateTimeOffset(2026, 10, 12, 15, 30, 0, TimeSpan.Zero);
        await SeedOtherTripAsync(fleet, departureAt, departureAt.AddMinutes(210), "Cancelled");

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(fleet.Token, Body(fleet), _now);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateSchedule_OwnTripsOverlapEachOther_IsRefusedNamingTheEarlierTrip()
    {
        // Arrange: a 30-hour route on Monday and Tuesday keeps the bus busy into Tuesday's departure.
        Fleet fleet = await SeedFleetAsync(journeyMinutes: 1800);

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(
            fleet.Token, Body(fleet) with { DaysOfWeek = new[] { "monday", "tuesday" } }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement conflicts = problem.RootElement.GetProperty("conflicts");
        Assert.Equal(2, conflicts.GetArrayLength());
        Assert.Equal("2026-10-13", conflicts[0].GetProperty("serviceDate").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 17, 0, 0, TimeSpan.Zero), conflicts[0].GetProperty("departureAt").GetDateTimeOffset());
        Assert.Equal("2026-10-20", conflicts[1].GetProperty("serviceDate").GetString());
        Assert.Equal("0 schedules, 0 trips", await BusSchedulesAsync(fleet.BusId));
    }

    [Fact]
    public async Task CreateSchedule_BusActivatedAgain_CanBeScheduled()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync(busStatus: "Inactive");
        using HttpResponseMessage activation = await ActivateBusAsync(fleet);

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(fleet.Token, Body(fleet), _now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateSchedule_BusDetailsChangedBefore_TripsCarryTheNewDetails()
    {
        // Arrange
        Fleet fleet = await SeedFleetAsync();
        await _database.SeedAsync(UpdateBusDetailsSql, new { fleet.BusId });

        // Act
        Guid scheduleId = await CreateAndReadIdAsync(fleet, Body(fleet), _now);

        // Assert
        using JsonDocument snapshot = await ReadEventDataAsync(scheduleId, "2026-10-12");
        Assert.Equal("Shivneri Sleeper", snapshot.RootElement.GetProperty("busName").GetString());
        Assert.False(snapshot.RootElement.GetProperty("isAc").GetBoolean());
        Assert.Equal("ChargingPoint", Join(snapshot.RootElement.GetProperty("amenities")));
    }

    [Fact]
    public async Task CreateSchedule_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());
        var body = new ScheduleBody { RouteId = Guid.NewGuid(), BusId = Guid.NewGuid() };

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(accessToken, body, _now);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task CreateSchedule_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);
        var body = new ScheduleBody { RouteId = Guid.NewGuid(), BusId = Guid.NewGuid() };

        // Act
        using HttpResponseMessage response = await CreateScheduleAsync(accessToken, body, _now);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CreateSchedule_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        var body = new ScheduleBody { RouteId = Guid.NewGuid(), BusId = Guid.NewGuid() };

        // Act
        using HttpResponseMessage response = await client.PostAsJsonAsync("/schedules", body);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static ScheduleBody Body(Fleet fleet)
    {
        return new ScheduleBody { RouteId = fleet.RouteId, BusId = fleet.BusId };
    }

    private static FareBody[] MismatchedFares(string firstClass, string? extraClass)
    {
        if (extraClass is null)
        {
            return new[] { new FareBody(firstClass, 1200) };
        }

        return new[] { new FareBody(firstClass, 1200), new FareBody("Upper berth", 1000), new FareBody(extraClass, 900) };
    }

    private static ScheduleBody Breaking(string rule, ScheduleBody valid)
    {
        switch (rule)
        {
            case "departure 24:00":
                return valid with { DepartureTime = "24:00" };
            case "no day":
                return valid with { DaysOfWeek = Array.Empty<string>() };
            case "monday twice":
                return valid with { DaysOfWeek = new[] { "monday", "monday" } };
            case "fare 49":
                return valid with { ClassFares = new[] { new FareBody("Lower berth", 49), new FareBody("Upper berth", 1000) } };
            case "fare 10001":
                return valid with { ClassFares = new[] { new FareBody("Lower berth", 10_001), new FareBody("Upper berth", 1000) } };
            case "fare 999.50":
                return valid with { ClassFares = new[] { new FareBody("Lower berth", 999.50m), new FareBody("Upper berth", 1000) } };
            case "lower berth twice":
                return valid with { ClassFares = new[] { new FareBody("Lower berth", 1200), new FareBody("lower berth", 1000) } };
            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule.");
        }
    }

    // The stored event data is jsonb, which re-spaces arrays; their items are compared instead.
    private static string Join(JsonElement strings)
    {
        IEnumerable<string?> items = strings.EnumerateArray().Select(item => item.GetString());
        return string.Join(",", items);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Fleet> SeedFleetAsync(string busStatus = "Active", string routeStatus = "Active", int journeyMinutes = 210)
    {
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        var busId = Guid.NewGuid();
        await _database.SeedAsync(InsertBusSql, new
        {
            BusId = busId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "TS" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            Status = busStatus,
        });

        var routeId = Guid.NewGuid();
        await _database.SeedAsync(InsertRouteSql, new
        {
            RouteId = routeId,
            OperatorId = operatorId.ToString(),
            Status = routeStatus,
            Mumbai = _mumbai,
            Lonavala = _lonavala,
            Pune = _pune,
            MidwayMinutes = journeyMinutes / 2,
            JourneyMinutes = journeyMinutes,
        });
        return new Fleet(operatorId, busId, routeId);
    }

    private async Task<Guid> SeedOtherTripAsync(Fleet fleet, DateTimeOffset departureAt, DateTimeOffset arrivalAt, string status)
    {
        var tripId = Guid.NewGuid();
        await _database.SeedAsync(InsertOtherTripSql, new
        {
            TripId = tripId,
            ScheduleId = Guid.NewGuid(),
            OperatorId = fleet.OperatorId.ToString(),
            fleet.RouteId,
            fleet.BusId,
            DepartureAt = departureAt,
            ArrivalAt = arrivalAt,
            Status = status,
        });
        return tripId;
    }

    private async Task<string?> BusSchedulesAsync(Guid busId)
    {
        return await _database.QueryScalarAsync<string>(BusSchedulesSql, new { BusId = busId });
    }

    private async Task<JsonDocument> ReadEventDataAsync(Guid scheduleId, string serviceDate)
    {
        string? data = await _database.QueryScalarAsync<string>(EventDataSql, new { ScheduleId = scheduleId, ServiceDate = serviceDate });
        return JsonDocument.Parse(data!);
    }

    private async Task<Guid> CreateAndReadIdAsync(Fleet fleet, ScheduleBody body, DateTimeOffset now)
    {
        using HttpResponseMessage response = await CreateScheduleAsync(fleet.Token, body, now);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var schedule = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return schedule.RootElement.GetProperty("scheduleId").GetGuid();
    }

    private async Task<HttpResponseMessage> CreateScheduleAsync(string accessToken, ScheduleBody body, DateTimeOffset now)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(now));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, "/schedules", accessToken, body);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "schedule-test-correlation");
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> ActivateBusAsync(Fleet fleet)
    {
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, $"/buses/{fleet.BusId}/activate", fleet.Token);
        return await client.SendAsync(request);
    }

    /// <summary>An operator with a profile, an active-or-not bus "Alpha" and a Mumbai -> Pune route.</summary>
    private sealed record Fleet(Guid OperatorId, Guid BusId, Guid RouteId)
    {
        public string Token
        {
            get { return TestAccessTokens.CreateOperator(OperatorId); }
        }
    }

    /// <summary>A createSchedule body; by default the schedule of the spec's examples, 22:30 on Monday, Wednesday and Friday.</summary>
    private sealed record ScheduleBody
    {
        public Guid RouteId { get; init; }

        public Guid BusId { get; init; }

        public string DepartureTime { get; init; } = "22:30";

        public string[] DaysOfWeek { get; init; } = new[] { "monday", "wednesday", "friday" };

        public FareBody[] ClassFares { get; init; } = new[] { new FareBody("Lower berth", 1200), new FareBody("Upper berth", 1000) };
    }

    private sealed record FareBody(string SeatClass, decimal Fare);
}
