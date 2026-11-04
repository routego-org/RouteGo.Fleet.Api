using System.Text.Json;
using RouteGo.Fleet.Api.Common.Behaviors;

namespace RouteGo.Fleet.Api.Features.Trips.ReplaceSchedule;

[Collection(DatabaseCollection.Name)]
public sealed class ReplaceScheduleTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    // Friday 2026-10-09: 10:00 IST, 18:00 IST (4.5 hours before the 22:30 trip) and 23:00 IST.
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 9, 4, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _evening = new DateTimeOffset(2026, 10, 9, 12, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _lateEvening = new DateTimeOffset(2026, 10, 9, 17, 30, 0, TimeSpan.Zero);

    // The trips schedule S1 got when it was created: 22:30 IST on Monday, Wednesday and Friday.
    private static readonly DateOnly[] _firstFortnight = new[]
    {
        new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14),
        new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 21),
    };

    private const string InsertBusSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, @Name, 'Sleeper', true, NULL, @Status);
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 5, 2, 1);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Lower berth', 1), (@BusId, 'Upper berth', 2);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'L1', 'Lower', 1, 1, 'Lower berth'), (@BusId, 'U1', 'Lower', 1, 2, 'Upper berth');
        """;

    private const string InsertRouteSql = """
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, @Status);
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Pune, 210);
        """;

    // Schedule S1, generated through 2026-10-22, "Lower berth" Rs 1,200 and "Upper berth" Rs 1,000 (in paise).
    private const string InsertScheduleSql = """
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '22:30', ARRAY['Monday', 'Wednesday', 'Friday'], @Status, '2026-10-22');
        INSERT INTO trips.schedule_class_fares (schedule_id, seat_class, fare)
        VALUES (@ScheduleId, 'Lower berth', 120000), (@ScheduleId, 'Upper berth', 100000);
        """;

    // Trips of S1 at 22:30 IST with the schedule's fares; a cancelled one carries the cause, reason
    // and time the trips table requires of every cancelled trip.
    private const string InsertTripsSql = """
        WITH inserted AS (
            INSERT INTO trips.trips (schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status,
                                     cancellation_cause, cancel_reason, cancelled_at)
            SELECT @ScheduleId, @OperatorId, @RouteId, @BusId, t.service_date,
                   (t.service_date + time '22:30') AT TIME ZONE 'Asia/Kolkata',
                   (t.service_date + time '22:30') AT TIME ZONE 'Asia/Kolkata' + interval '210 minutes',
                   CASE WHEN @Cause IS NULL THEN 'Scheduled' ELSE 'Cancelled' END,
                   @Cause,
                   CASE WHEN @Cause IS NOT NULL THEN 'Bus sent for repair' END,
                   CASE WHEN @Cause IS NOT NULL THEN timestamptz '2026-10-08T10:00:00Z' END
            FROM unnest(@ServiceDates) AS t(service_date)
            RETURNING trip_id
        )
        INSERT INTO trips.trip_class_fares (trip_id, seat_class, fare)
        SELECT i.trip_id, f.seat_class, f.fare
        FROM inserted i
        CROSS JOIN (VALUES ('Lower berth', 120000::bigint), ('Upper berth', 100000::bigint)) AS f(seat_class, fare);
        """;

    // A trip of another schedule on the given bus.
    private const string InsertOtherTripSql = """
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@OtherScheduleId, @OperatorId, @RouteId, @BusId, '21:00', ARRAY['Monday'], 'Active', '2026-10-22');
        INSERT INTO trips.trips (trip_id, schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status)
        VALUES (@TripId, @OtherScheduleId, @OperatorId, @RouteId, @BusId, '2026-10-12', @DepartureAt, @DepartureAt + interval '210 minutes', 'Scheduled');
        """;

    // Each trip of the schedule as "date time(IST) status/cause", in date then departure order.
    private const string TripStatesSql = """
        SELECT string_agg(service_date || ' ' || to_char(departure_at AT TIME ZONE 'Asia/Kolkata', 'HH24:MI') || ' '
                          || status || coalesce('/' || cancellation_cause, ''), ',' ORDER BY service_date, departure_at)
        FROM trips.trips WHERE schedule_id = @ScheduleId
        """;

    private const string ScheduledDatesSql = """
        SELECT coalesce(string_agg(service_date::text, ',' ORDER BY service_date), '-')
        FROM trips.trips WHERE schedule_id = @ScheduleId AND status = 'Scheduled'
        """;

    // Each scheduled trip's fares in paise as "date:lower/upper".
    private const string TripFaresSql = """
        SELECT string_agg(t.service_date || ':' || f.fares, ',' ORDER BY t.service_date)
        FROM trips.trips t
        JOIN LATERAL (SELECT string_agg(fare::text, '/' ORDER BY seat_class) AS fares
                      FROM trips.trip_class_fares WHERE trip_id = t.trip_id) f ON true
        WHERE t.schedule_id = @ScheduleId AND t.status = 'Scheduled'
        """;

    // Events per "type/reason/correlation=count".
    private const string EventSummarySql = """
        SELECT coalesce(string_agg(summary, ',' ORDER BY summary), '-')
        FROM (SELECT e.event_type || '/' || coalesce(e.data ->> 'reason', '-') || '/' || e.correlation_id || '=' || count(*) AS summary
              FROM trips.fleet_events e JOIN trips.trips t ON t.trip_id = e.trip_id
              WHERE t.schedule_id = @ScheduleId
              GROUP BY e.event_type, e.data ->> 'reason', e.correlation_id) s
        """;

    private const string EventDatesSql = """
        SELECT coalesce(string_agg(DISTINCT t.service_date::text, ',' ORDER BY t.service_date::text), '-')
        FROM trips.fleet_events e JOIN trips.trips t ON t.trip_id = e.trip_id
        WHERE t.schedule_id = @ScheduleId
        """;

    private const string EventTripIdSql = """
        SELECT e.trip_id
        FROM trips.fleet_events e JOIN trips.trips t ON t.trip_id = e.trip_id
        WHERE t.schedule_id = @ScheduleId AND t.service_date = @ServiceDate::date AND e.event_type = @EventType
        """;

    private const string EventDataSql = """
        SELECT e.data::text
        FROM trips.fleet_events e JOIN trips.trips t ON t.trip_id = e.trip_id
        WHERE t.schedule_id = @ScheduleId AND t.service_date = @ServiceDate::date AND e.event_type = @EventType
        """;

    private const string ScheduleBusSql = """
        SELECT bus_id FROM trips.schedules WHERE schedule_id = @ScheduleId
        """;

    private const string ScheduleFaresSql = """
        SELECT string_agg(seat_class || '=' || fare, ',' ORDER BY seat_class)
        FROM trips.schedule_class_fares WHERE schedule_id = @ScheduleId
        """;

    private readonly DatabaseFixture _database;

    public ReplaceScheduleTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task ReplaceSchedule_DepartureTimeChanged_ReportsSixCreatedAndSixCancelled()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet) with { DepartureTime = "23:00" }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var change = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement schedule = change.RootElement.GetProperty("schedule");
        Assert.Equal(fleet.ScheduleId, schedule.GetProperty("scheduleId").GetGuid());
        Assert.Equal("23:00", schedule.GetProperty("departureTime").GetString());
        Assert.Equal("[\"monday\",\"wednesday\",\"friday\"]", schedule.GetProperty("daysOfWeek").GetRawText());
        Assert.Equal("active", schedule.GetProperty("status").GetString());
        Assert.Equal("2026-10-22", schedule.GetProperty("tripsGeneratedThrough").GetString());
        Assert.Equal(6, change.RootElement.GetProperty("createdTripCount").GetInt32());
        Assert.Equal(6, change.RootElement.GetProperty("cancelledTripCount").GetInt32());
        Assert.Equal(0, change.RootElement.GetProperty("repricedTripCount").GetInt32());
    }

    [Fact]
    public async Task ReplaceSchedule_DepartureTimeChanged_CancelsTheOldTripsAndGeneratesNewOnesWithTheirEvents()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet) with { DepartureTime = "23:00" }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "2026-10-09 22:30 Cancelled/ScheduleChanged,2026-10-09 23:00 Scheduled,2026-10-12 22:30 Cancelled/ScheduleChanged,"
            + "2026-10-12 23:00 Scheduled,2026-10-14 22:30 Cancelled/ScheduleChanged,2026-10-14 23:00 Scheduled,"
            + "2026-10-16 22:30 Cancelled/ScheduleChanged,2026-10-16 23:00 Scheduled,2026-10-19 22:30 Cancelled/ScheduleChanged,"
            + "2026-10-19 23:00 Scheduled,2026-10-21 22:30 Cancelled/ScheduleChanged,2026-10-21 23:00 Scheduled",
            await QueryAsync(TripStatesSql, fleet));
        Assert.Equal(
            "TripCancelled/Schedule changed/replace-test-correlation=6,TripScheduled/-/replace-test-correlation=6",
            await QueryAsync(EventSummarySql, fleet));
    }

    [Fact]
    public async Task ReplaceSchedule_DepartureTimeChanged_AnnouncesEachNewTripWithItsSnapshot()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet) with { DepartureTime = "23:00" }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RecordedEvent recorded = await ReadEventAsync(fleet, "2026-10-09", "TripScheduled");
        using var data = JsonDocument.Parse(recorded.Data);
        JsonElement snapshot = data.RootElement;
        Assert.Equal(recorded.TripId, snapshot.GetProperty("tripId").GetGuid());
        Assert.Equal(fleet.ScheduleId, snapshot.GetProperty("scheduleId").GetGuid());
        Assert.Equal("BusCo Travels", snapshot.GetProperty("operatorName").GetString());
        Assert.Equal("Alpha", snapshot.GetProperty("busName").GetString());
        Assert.Equal("2026-10-09T17:30:00Z", snapshot.GetProperty("departureUtc").GetString());
        Assert.Equal("2026-10-09T21:00:00Z", snapshot.GetProperty("arrivalUtc").GetString());
        Assert.Equal("Lower berth", snapshot.GetProperty("seats")[0].GetProperty("class").GetString());
        Assert.Equal(1200, snapshot.GetProperty("seats")[0].GetProperty("fare").GetInt32());
    }

    [Fact]
    public async Task ReplaceSchedule_DaysChangedToMondayAndSaturday_ChangesOnlyTheAffectedDates()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());
        ScheduleBody body = Body(fleet) with { DaysOfWeek = new[] { "monday", "saturday" } };

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, body, _now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var change = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2026-10-22", change.RootElement.GetProperty("schedule").GetProperty("tripsGeneratedThrough").GetString());
        Assert.Equal(2, change.RootElement.GetProperty("createdTripCount").GetInt32());
        Assert.Equal(4, change.RootElement.GetProperty("cancelledTripCount").GetInt32());
        Assert.Equal("2026-10-10,2026-10-12,2026-10-17,2026-10-19", await QueryAsync(ScheduledDatesSql, fleet));
        Assert.Equal("2026-10-09,2026-10-10,2026-10-14,2026-10-16,2026-10-17,2026-10-21", await QueryAsync(EventDatesSql, fleet));
    }

    [Fact]
    public async Task ReplaceSchedule_FareOnlyChangeAtSixPm_RepricesAllButTheTripInsideTheCutoff()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet) with { ClassFares = _newFares }, _evening);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var change = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "[{\"seatClass\":\"Lower berth\",\"fare\":1400},{\"seatClass\":\"Upper berth\",\"fare\":1100}]",
            change.RootElement.GetProperty("schedule").GetProperty("classFares").GetRawText());
        Assert.Equal(0, change.RootElement.GetProperty("createdTripCount").GetInt32());
        Assert.Equal(0, change.RootElement.GetProperty("cancelledTripCount").GetInt32());
        Assert.Equal(5, change.RootElement.GetProperty("repricedTripCount").GetInt32());
        Assert.Equal(
            "2026-10-09:120000/100000,2026-10-12:140000/110000,2026-10-14:140000/110000,"
            + "2026-10-16:140000/110000,2026-10-19:140000/110000,2026-10-21:140000/110000",
            await QueryAsync(TripFaresSql, fleet));
        Assert.Equal("FareChanged/-/replace-test-correlation=5", await QueryAsync(EventSummarySql, fleet));
        Assert.Equal("Lower berth=140000,Upper berth=110000", await QueryAsync(ScheduleFaresSql, fleet));
    }

    [Fact]
    public async Task ReplaceSchedule_FareOnlyChange_RecordsTheNewFaresInFareChanged()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet) with { ClassFares = _newFares }, _evening);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RecordedEvent recorded = await ReadEventAsync(fleet, "2026-10-12", "FareChanged");
        using var data = JsonDocument.Parse(recorded.Data);
        JsonElement fareChange = data.RootElement;
        Assert.Equal(recorded.TripId, fareChange.GetProperty("tripId").GetGuid());
        Assert.Equal("Lower berth", fareChange.GetProperty("classFares")[0].GetProperty("class").GetString());
        Assert.Equal(1400, fareChange.GetProperty("classFares")[0].GetProperty("fare").GetInt32());
        Assert.Equal("Upper berth", fareChange.GetProperty("classFares")[1].GetProperty("class").GetString());
        Assert.Equal(1100, fareChange.GetProperty("classFares")[1].GetProperty("fare").GetInt32());
        Assert.Equal(2, fareChange.GetProperty("seats").GetArrayLength());
        Assert.Equal("U1", fareChange.GetProperty("seats")[1].GetProperty("seatNo").GetString());
        Assert.Equal("Upper berth", fareChange.GetProperty("seats")[1].GetProperty("class").GetString());
        Assert.Equal(1100, fareChange.GetProperty("seats")[1].GetProperty("fare").GetInt32());
    }

    [Fact]
    public async Task ReplaceSchedule_SameDetails_ChangesNoTripAndRecordsNoEvent()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet), _now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var change = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, change.RootElement.GetProperty("createdTripCount").GetInt32());
        Assert.Equal(0, change.RootElement.GetProperty("cancelledTripCount").GetInt32());
        Assert.Equal(0, change.RootElement.GetProperty("repricedTripCount").GetInt32());
        Assert.Equal(_firstFortnightText, await QueryAsync(ScheduledDatesSql, fleet));
        Assert.Equal("-", await QueryAsync(EventSummarySql, fleet));
    }

    [Fact]
    public async Task ReplaceSchedule_DepartedAndOperatorCancelledTrips_AreNeverTouchedNorRegenerated()
    {
        // Arrange: at 23:00 IST the 2026-10-09 trip has departed; the operator cancelled 2026-10-14.
        Fleet fleet = await SeedScheduleAsync(new SeedOptions { TripDates = _fortnightWithoutTheFourteenth });
        await SeedTripsAsync(fleet, new[] { new DateOnly(2026, 10, 14) }, "Operator");

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet) with { DepartureTime = "23:30" }, _lateEvening);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "2026-10-09 22:30 Scheduled,2026-10-12 22:30 Cancelled/ScheduleChanged,2026-10-12 23:30 Scheduled,"
            + "2026-10-14 22:30 Cancelled/Operator,2026-10-16 22:30 Cancelled/ScheduleChanged,2026-10-16 23:30 Scheduled,"
            + "2026-10-19 22:30 Cancelled/ScheduleChanged,2026-10-19 23:30 Scheduled,2026-10-21 22:30 Cancelled/ScheduleChanged,"
            + "2026-10-21 23:30 Scheduled",
            await QueryAsync(TripStatesSql, fleet));
    }

    [Theory]
    [InlineData("Lower berth", null, "[\"Upper berth\"]", "[]")]
    [InlineData("Lower berth", "Window", "[]", "[\"Window\"]")]
    public async Task ReplaceSchedule_FaresNotMatchingTheBus_IsRefusedClassFaresMismatchAndNothingChanges(
        string firstClass, string? extraClass, string missingClasses, string unknownClasses)
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet) with { ClassFares = MismatchedFares(firstClass, extraClass) }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CLASS_FARES_MISMATCH", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(missingClasses, problem.RootElement.GetProperty("missingClasses").GetRawText());
        Assert.Equal(unknownClasses, problem.RootElement.GetProperty("unknownClasses").GetRawText());
        Assert.Equal("Lower berth=120000,Upper berth=100000", await QueryAsync(ScheduleFaresSql, fleet));
        Assert.Equal("-", await QueryAsync(EventSummarySql, fleet));
    }

    [Theory]
    [InlineData("departure 24:00", "departureTime")]
    [InlineData("no day", "daysOfWeek")]
    [InlineData("monday twice", "daysOfWeek")]
    [InlineData("fare 49", "classFares[0].fare")]
    [InlineData("fare 10001", "classFares[0].fare")]
    [InlineData("fare 999.50", "classFares[0].fare")]
    [InlineData("lower berth twice", "classFares[1].seatClass")]
    public async Task ReplaceSchedule_FieldBreakingARule_IsRefusedNamingTheField(string rule, string field)
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Breaking(rule, Body(fleet)), _now);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), $"No error for {field}.");
    }

    [Fact]
    public async Task ReplaceSchedule_UnknownOrAnotherOperatorsBus_IsRefusedUnknownBus()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());
        Fleet otherFleet = await SeedScheduleAsync(new SeedOptions { TripDates = Array.Empty<DateOnly>() });
        var unknownBusId = Guid.NewGuid();

        // Act
        using HttpResponseMessage unknown = await ReplaceAsync(fleet, Body(fleet) with { BusId = unknownBusId }, _now);
        using HttpResponseMessage others = await ReplaceAsync(fleet, Body(fleet) with { BusId = otherFleet.BusId }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        using var problem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        Assert.Equal("UNKNOWN_BUS", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(unknownBusId, problem.RootElement.GetProperty("busId").GetGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, others.StatusCode);
        Assert.Equal("UNKNOWN_BUS", await ProblemCodeAsync(others));
    }

    [Fact]
    public async Task ReplaceSchedule_UnknownOrAnotherOperatorsRoute_IsRefusedUnknownRoute()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());
        Fleet otherFleet = await SeedScheduleAsync(new SeedOptions { TripDates = Array.Empty<DateOnly>() });
        var unknownRouteId = Guid.NewGuid();

        // Act
        using HttpResponseMessage unknown = await ReplaceAsync(fleet, Body(fleet) with { RouteId = unknownRouteId }, _now);
        using HttpResponseMessage others = await ReplaceAsync(fleet, Body(fleet) with { RouteId = otherFleet.RouteId }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        using var problem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        Assert.Equal("UNKNOWN_ROUTE", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(unknownRouteId, problem.RootElement.GetProperty("routeId").GetGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, others.StatusCode);
        Assert.Equal("UNKNOWN_ROUTE", await ProblemCodeAsync(others));
    }

    [Fact]
    public async Task ReplaceSchedule_InactiveBus_IsRefusedBusInactive()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions { BusStatus = "Inactive", TripDates = Array.Empty<DateOnly>() });

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet), _now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BUS_INACTIVE", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(fleet.BusId, problem.RootElement.GetProperty("busId").GetGuid());
    }

    [Fact]
    public async Task ReplaceSchedule_MovedToAnInactiveRoute_IsRefusedRouteInactive()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());
        Guid inactiveRouteId = await SeedRouteAsync(fleet.OperatorId, "Inactive");

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet) with { RouteId = inactiveRouteId }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ROUTE_INACTIVE", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(inactiveRouteId, problem.RootElement.GetProperty("routeId").GetGuid());
        Assert.Equal("-", await QueryAsync(EventSummarySql, fleet));
    }

    [Fact]
    public async Task ReplaceSchedule_ItsOwnRouteDeactivatedSince_IsStillAccepted()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions { RouteStatus = "Inactive" });

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet) with { DepartureTime = "23:00" }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var change = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(6, change.RootElement.GetProperty("createdTripCount").GetInt32());
    }

    [Fact]
    public async Task ReplaceSchedule_BusOnAnotherTripAtTheTime_IsRefusedBusTripOverlapAndNothingChanges()
    {
        // Arrange: S1 runs on "Beta"; "Alpha" has another schedule's trip on 2026-10-12, 21:00 to 00:30 IST.
        Fleet fleet = await SeedScheduleAsync(new SeedOptions { BusName = "Beta" });
        Guid alphaId = await SeedBusAsync(fleet.OperatorId, "Alpha", "Active");
        Guid otherTripId = await SeedOtherTripAsync(fleet, alphaId);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet) with { BusId = alphaId }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement conflict = problem.RootElement.GetProperty("conflicts")[0];
        Assert.Equal("BUS_TRIP_OVERLAP", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, problem.RootElement.GetProperty("conflicts").GetArrayLength());
        Assert.Equal("2026-10-12", conflict.GetProperty("serviceDate").GetString());
        Assert.Equal(otherTripId, conflict.GetProperty("tripId").GetGuid());
        Assert.Equal(_otherTripDeparture, conflict.GetProperty("departureAt").GetDateTimeOffset());
        Assert.Equal(fleet.BusId, await _database.QueryScalarAsync<Guid>(ScheduleBusSql, new { fleet.ScheduleId }));
        Assert.Equal(_firstFortnightText, await QueryAsync(ScheduledDatesSql, fleet));
        Assert.Equal("-", await QueryAsync(EventSummarySql, fleet));
    }

    [Fact]
    public async Task ReplaceSchedule_InactiveSchedule_IsRefusedScheduleInactive()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions { ScheduleStatus = "Inactive" });

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet) with { DepartureTime = "23:00" }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("SCHEDULE_INACTIVE", await ProblemCodeAsync(response));
        Assert.Equal("-", await QueryAsync(EventSummarySql, fleet));
    }

    [Fact]
    public async Task ReplaceSchedule_ScheduleActivatedAgain_CanBeEdited()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions { ScheduleStatus = "Inactive" });
        using HttpResponseMessage activation = await SendAsync(fleet.Token, HttpMethod.Post, $"/schedules/{fleet.ScheduleId}/activate", null);

        // Act
        using HttpResponseMessage response = await ReplaceAsync(fleet, Body(fleet) with { DepartureTime = "23:00" }, _now);

        // Assert
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReplaceSchedule_UnknownSchedule_IsRefusedNotFound()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions { TripDates = Array.Empty<DateOnly>() });

        // Act
        using HttpResponseMessage response = await SendAsync(fleet.Token, HttpMethod.Put, $"/schedules/{Guid.NewGuid()}", Body(fleet));

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ReplaceSchedule_AnotherOperatorsSchedule_IsReportedNotFoundAndLeftAlone()
    {
        // Arrange
        Fleet fleet = await SeedScheduleAsync(new SeedOptions());
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);
        string otherToken = TestAccessTokens.CreateOperator(otherOperator);

        // Act
        using HttpResponseMessage response = await SendAsync(
            otherToken, HttpMethod.Put, $"/schedules/{fleet.ScheduleId}", Body(fleet) with { DepartureTime = "23:00" });

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal(_firstFortnightText, await QueryAsync(ScheduledDatesSql, fleet));
        Assert.Equal("-", await QueryAsync(EventSummarySql, fleet));
    }

    [Fact]
    public async Task ReplaceSchedule_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());
        var body = new ScheduleBody { RouteId = Guid.NewGuid(), BusId = Guid.NewGuid() };

        // Act
        using HttpResponseMessage response = await SendAsync(accessToken, HttpMethod.Put, $"/schedules/{Guid.NewGuid()}", body);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task ReplaceSchedule_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);
        var body = new ScheduleBody { RouteId = Guid.NewGuid(), BusId = Guid.NewGuid() };

        // Act
        using HttpResponseMessage response = await SendAsync(accessToken, HttpMethod.Put, $"/schedules/{Guid.NewGuid()}", body);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task ReplaceSchedule_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PutAsync($"/schedules/{Guid.NewGuid()}", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static readonly FareBody[] _newFares = new[] { new FareBody("Lower berth", 1400), new FareBody("Upper berth", 1100) };

    private static readonly DateOnly[] _fortnightWithoutTheFourteenth = new[]
    {
        new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 16),
        new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 21),
    };

    private static readonly string _firstFortnightText = string.Join(",", _firstFortnight.Select(date => date.ToString("yyyy-MM-dd")));

    // 2026-10-12 21:00 IST.
    private static readonly DateTimeOffset _otherTripDeparture = new DateTimeOffset(2026, 10, 12, 15, 30, 0, TimeSpan.Zero);

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

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Fleet> SeedScheduleAsync(SeedOptions options)
    {
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        Guid busId = await SeedBusAsync(operatorId, options.BusName, options.BusStatus);
        Guid routeId = await SeedRouteAsync(operatorId, options.RouteStatus);
        var fleet = new Fleet { OperatorId = operatorId, ScheduleId = Guid.NewGuid(), BusId = busId, RouteId = routeId };
        await _database.SeedAsync(InsertScheduleSql, new
        {
            fleet.ScheduleId,
            OperatorId = operatorId.ToString(),
            RouteId = routeId,
            BusId = busId,
            Status = options.ScheduleStatus,
        });
        await SeedTripsAsync(fleet, options.TripDates, null);
        return fleet;
    }

    private async Task<Guid> SeedBusAsync(Guid operatorId, string name, string status)
    {
        var busId = Guid.NewGuid();
        await _database.SeedAsync(InsertBusSql, new
        {
            BusId = busId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "TR" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            Name = name,
            Status = status,
        });
        return busId;
    }

    private async Task<Guid> SeedRouteAsync(Guid operatorId, string status)
    {
        var routeId = Guid.NewGuid();
        await _database.SeedAsync(InsertRouteSql, new
        {
            RouteId = routeId,
            OperatorId = operatorId.ToString(),
            Status = status,
            Mumbai = _mumbai,
            Pune = _pune,
        });
        return routeId;
    }

    private async Task SeedTripsAsync(Fleet fleet, DateOnly[] serviceDates, string? cause)
    {
        await _database.SeedAsync(InsertTripsSql, new
        {
            fleet.ScheduleId,
            OperatorId = fleet.OperatorId.ToString(),
            fleet.RouteId,
            fleet.BusId,
            ServiceDates = serviceDates,
            Cause = cause,
        });
    }

    private async Task<Guid> SeedOtherTripAsync(Fleet fleet, Guid busId)
    {
        var tripId = Guid.NewGuid();
        await _database.SeedAsync(InsertOtherTripSql, new
        {
            OtherScheduleId = Guid.NewGuid(),
            TripId = tripId,
            OperatorId = fleet.OperatorId.ToString(),
            fleet.RouteId,
            BusId = busId,
            DepartureAt = _otherTripDeparture,
        });
        return tripId;
    }

    private async Task<string?> QueryAsync(string sql, Fleet fleet)
    {
        return await _database.QueryScalarAsync<string>(sql, new { fleet.ScheduleId });
    }

    private async Task<RecordedEvent> ReadEventAsync(Fleet fleet, string serviceDate, string eventType)
    {
        var lookup = new { fleet.ScheduleId, ServiceDate = serviceDate, EventType = eventType };
        Guid tripId = await _database.QueryScalarAsync<Guid>(EventTripIdSql, lookup);
        string? data = await _database.QueryScalarAsync<string>(EventDataSql, lookup);
        return new RecordedEvent(tripId, data!);
    }

    private async Task<HttpResponseMessage> ReplaceAsync(Fleet fleet, ScheduleBody body, DateTimeOffset now)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(now));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Put, $"/schedules/{fleet.ScheduleId}", fleet.Token, body);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "replace-test-correlation");
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(string accessToken, HttpMethod method, string route, object? body)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(_now));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(method, route, accessToken, body);
        return await client.SendAsync(request);
    }

    /// <summary>An operator with a profile, its bus and route, and schedule S1 on them.</summary>
    private sealed class Fleet
    {
        public required Guid OperatorId { get; init; }

        public required Guid ScheduleId { get; init; }

        public required Guid BusId { get; init; }

        public required Guid RouteId { get; init; }

        public string Token
        {
            get { return TestAccessTokens.CreateOperator(OperatorId); }
        }
    }

    /// <summary>How to seed S1; by default everything active, on bus "Alpha", with its first fortnight of trips.</summary>
    private sealed class SeedOptions
    {
        public string BusName { get; init; } = "Alpha";

        public string BusStatus { get; init; } = "Active";

        public string RouteStatus { get; init; } = "Active";

        public string ScheduleStatus { get; init; } = "Active";

        public DateOnly[] TripDates { get; init; } = _firstFortnight;
    }

    /// <summary>A recorded event's trip and its data as stored.</summary>
    /// <param name="TripId">The trip the event is about.</param>
    /// <param name="Data">The event data, as JSON.</param>
    private sealed record RecordedEvent(Guid TripId, string Data);

    /// <summary>A replaceSchedule body; by default S1's own details, 22:30 on Monday, Wednesday and Friday.</summary>
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
