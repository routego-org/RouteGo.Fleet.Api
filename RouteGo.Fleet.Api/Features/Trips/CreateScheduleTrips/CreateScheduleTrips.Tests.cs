using System.Text.Json;
using RouteGo.Fleet.Api.Common.Behaviors;

namespace RouteGo.Fleet.Api.Features.Trips.CreateScheduleTrips;

[Collection(DatabaseCollection.Name)]
public sealed class CreateScheduleTripsTests
{
    // Fixed by the city seed migration, identical in every environment.
    private static readonly Guid _mumbai = Guid.Parse("ffe095c5-e8b5-4c45-93f9-b0d0aa8d6a37");
    private static readonly Guid _pune = Guid.Parse("0484575c-a368-4057-896d-c99d78ba9cce");

    // Friday 2026-10-09, 10:00 IST.
    private static readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 9, 4, 30, 0, TimeSpan.Zero);

    // The trips schedule S1 got when it was created: 22:30 IST on Monday, Wednesday and Friday.
    private static readonly DateOnly[] _firstFortnight = new[]
    {
        new DateOnly(2026, 10, 9), new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 14),
        new DateOnly(2026, 10, 16), new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 21),
    };

    // Bus "Alpha" with "Lower berth" and "Upper berth", route Mumbai -> Pune of 210 minutes, and
    // schedule S1 generated through 2026-10-22 with the given fares (in paise).
    private const string InsertScheduleSql = """
        INSERT INTO buses.buses (bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@BusId, @OperatorId, @RegistrationNumber, 'Alpha', 'Sleeper', true, NULL, @BusStatus);
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        VALUES (@BusId, 'Lower', 5, 2, 1);
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        VALUES (@BusId, 'Lower berth', 1), (@BusId, 'Upper berth', 2);
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        VALUES (@BusId, 'L1', 'Lower', 1, 1, 'Lower berth'), (@BusId, 'U1', 'Lower', 1, 2, 'Upper berth');
        INSERT INTO routes.routes (route_id, operator_id, distance_km, status)
        VALUES (@RouteId, @OperatorId, 150, @RouteStatus);
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        VALUES (@RouteId, 1, @Mumbai, 0), (@RouteId, 2, @Pune, 210);
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, '22:30', ARRAY['Monday', 'Wednesday', 'Friday'], @ScheduleStatus, '2026-10-22');
        INSERT INTO trips.schedule_class_fares (schedule_id, seat_class, fare)
        SELECT @ScheduleId, f.seat_class, f.fare FROM unnest(@FareClasses, @FarePaise) AS f(seat_class, fare);
        """;

    // Trips of a schedule at 22:30 IST on the given dates; a cancelled one carries the cause, reason
    // and time the trips table requires of every cancelled trip.
    private const string InsertTripsSql = """
        INSERT INTO trips.trips (schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status,
                                 cancellation_cause, cancel_reason, cancelled_at)
        SELECT @ScheduleId, @OperatorId, @RouteId, @BusId, t.service_date,
               (t.service_date + time '22:30') AT TIME ZONE 'Asia/Kolkata',
               (t.service_date + time '22:30') AT TIME ZONE 'Asia/Kolkata' + interval '210 minutes',
               CASE WHEN @Cause IS NULL THEN 'Scheduled' ELSE 'Cancelled' END,
               @Cause,
               CASE WHEN @Cause = 'Operator' THEN 'Bus sent for repair' WHEN @Cause IS NOT NULL THEN 'Schedule deactivated' END,
               CASE WHEN @Cause IS NOT NULL THEN timestamptz '2026-10-08T10:00:00Z' END
        FROM unnest(@ServiceDates) AS t(service_date);
        """;

    // A trip of another schedule on the same bus.
    private const string InsertOtherTripSql = """
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@OtherScheduleId, @OperatorId, @RouteId, @BusId, '22:00', ARRAY['Monday'], 'Active', '2026-10-26');
        INSERT INTO trips.trips (trip_id, schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status)
        VALUES (@TripId, @OtherScheduleId, @OperatorId, @RouteId, @BusId, '2026-10-26', @DepartureAt, @DepartureAt + interval '210 minutes', 'Scheduled');
        """;

    private const string UpdateBusNameSql = """
        UPDATE buses.buses SET name = 'Shivneri Sleeper' WHERE bus_id = @BusId
        """;

    private const string GeneratedThroughSql = """
        SELECT trips_generated_through::text FROM trips.schedules WHERE schedule_id = @ScheduleId
        """;

    private const string ScheduledDatesSql = """
        SELECT coalesce(string_agg(service_date::text, ',' ORDER BY service_date), '-')
        FROM trips.trips WHERE schedule_id = @ScheduleId AND status = 'Scheduled'
        """;

    private const string NewTripFaresSql = """
        SELECT string_agg(f.seat_class || '=' || f.fare, ',' ORDER BY f.seat_class)
        FROM trips.trip_class_fares f JOIN trips.trips t ON t.trip_id = f.trip_id
        WHERE t.schedule_id = @ScheduleId AND t.service_date = '2026-11-06'
        """;

    private const string EventSummarySql = """
        SELECT count(*) || ' ' || coalesce(string_agg(DISTINCT e.event_type || '/' || e.correlation_id, ','), '-')
        FROM trips.fleet_events e JOIN trips.trips t ON t.trip_id = e.trip_id
        WHERE t.schedule_id = @ScheduleId
        """;

    private const string EventBusNameSql = """
        SELECT e.data ->> 'busName'
        FROM trips.fleet_events e JOIN trips.trips t ON t.trip_id = e.trip_id
        WHERE t.schedule_id = @ScheduleId AND t.service_date = '2026-10-23'
        """;

    private readonly DatabaseFixture _database;

    public CreateScheduleTripsTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public async Task CreateScheduleTrips_ExtendToNovemberSixth_GeneratesTheSevenMissingTrips()
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-11-06");

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var extension = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = extension.RootElement;
        Assert.Equal(seeded.ScheduleId, root.GetProperty("scheduleId").GetGuid());
        Assert.Equal("2026-11-06", root.GetProperty("tripsGeneratedThrough").GetString());
        Assert.Equal("2026-10-23,2026-10-26,2026-10-28,2026-10-30,2026-11-02,2026-11-04,2026-11-06", ServiceDates(root));
        Assert.Equal("2026-11-06", await GeneratedThroughAsync(seeded.ScheduleId));
        Assert.Equal("Lower berth=120000,Upper berth=100000",
            await _database.QueryScalarAsync<string>(NewTripFaresSql, new { seeded.ScheduleId }));
        Assert.Equal("7 TripScheduled/extend-test-correlation",
            await _database.QueryScalarAsync<string>(EventSummarySql, new { seeded.ScheduleId }));
    }

    [Fact]
    public async Task CreateScheduleTrips_ExtendToNovemberSixth_ListsEachNewTripInFull()
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-11-06");

        // Assert
        using var extension = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement first = extension.RootElement.GetProperty("items")[0];
        Assert.Equal(seeded.ScheduleId, first.GetProperty("scheduleId").GetGuid());
        Assert.Equal(seeded.OperatorId.ToString(), first.GetProperty("operatorId").GetString());
        Assert.Equal("BusCo Travels", first.GetProperty("operatorName").GetString());
        Assert.Equal(seeded.RouteId, first.GetProperty("routeId").GetGuid());
        Assert.Equal("Mumbai", first.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal(_pune, first.GetProperty("destination").GetProperty("cityId").GetGuid());
        Assert.Equal(seeded.BusId, first.GetProperty("busId").GetGuid());
        Assert.Equal("Alpha", first.GetProperty("busName").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 17, 0, 0, TimeSpan.Zero), first.GetProperty("departureAt").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 20, 30, 0, TimeSpan.Zero), first.GetProperty("arrivalAt").GetDateTimeOffset());
        Assert.Equal("scheduled", first.GetProperty("status").GetString());
        Assert.Equal(
            "[{\"seatClass\":\"Lower berth\",\"fare\":1200},{\"seatClass\":\"Upper berth\",\"fare\":1000}]",
            first.GetProperty("classFares").GetRawText());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("cancelReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("cancelledAt").ValueKind);
    }

    [Theory]
    [InlineData("2026-11-06")]
    [InlineData("2026-10-30")]
    public async Task CreateScheduleTrips_AlreadyExtendedToNovemberSixth_GeneratesNothingMore(string endDate)
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions());
        using HttpResponseMessage first = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-11-06");

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, endDate);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var extension = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, extension.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("2026-11-06", extension.RootElement.GetProperty("tripsGeneratedThrough").GetString());
        Assert.Equal("2026-11-06", await GeneratedThroughAsync(seeded.ScheduleId));
        Assert.Equal("7 TripScheduled/extend-test-correlation",
            await _database.QueryScalarAsync<string>(EventSummarySql, new { seeded.ScheduleId }));
    }

    [Fact]
    public async Task CreateScheduleTrips_DateTheOperatorCancelled_IsNotBroughtBack()
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions());
        await SeedTripsAsync(seeded, new[] { new DateOnly(2026, 10, 26) }, "Operator");

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-11-06");

        // Assert
        using var extension = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2026-10-23,2026-10-28,2026-10-30,2026-11-02,2026-11-04,2026-11-06", ServiceDates(extension.RootElement));
    }

    [Fact]
    public async Task CreateScheduleTrips_DatesCancelledByDeactivation_AreGeneratedAgain()
    {
        // Arrange: S1 was deactivated (cancelling its fortnight) and activated again.
        Seeded seeded = await SeedScheduleAsync(new SeedOptions { HasFirstFortnight = false });
        await SeedTripsAsync(seeded, _firstFortnight, "ScheduleDeactivated");

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-10-22");

        // Assert
        using var extension = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2026-10-09,2026-10-12,2026-10-14,2026-10-16,2026-10-19,2026-10-21", ServiceDates(extension.RootElement));
        Assert.Equal("2026-10-22", extension.RootElement.GetProperty("tripsGeneratedThrough").GetString());
    }

    [Theory]
    [InlineData("2026-10-08")]
    [InlineData("2026-12-09")]
    public async Task CreateScheduleTrips_EndDateOutOfRange_IsRefusedWithTheLatestAllowedDate(string endDate)
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, endDate);

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TRIP_END_DATE_OUT_OF_RANGE", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("2026-12-08", problem.RootElement.GetProperty("latestAllowedDate").GetString());
        Assert.Equal("2026-10-22", await GeneratedThroughAsync(seeded.ScheduleId));
    }

    [Fact]
    public async Task CreateScheduleTrips_EndDateExactlySixtyDaysAhead_IsAccepted()
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-12-08");

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("2026-12-08", await GeneratedThroughAsync(seeded.ScheduleId));
    }

    [Fact]
    public async Task CreateScheduleTrips_InactiveSchedule_IsRefusedScheduleInactive()
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions { ScheduleStatus = "Inactive" });

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-11-06");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("SCHEDULE_INACTIVE", await ProblemCodeAsync(response));
        Assert.Equal("2026-10-22", await GeneratedThroughAsync(seeded.ScheduleId));
    }

    [Fact]
    public async Task CreateScheduleTrips_InactiveBus_IsRefusedBusInactive()
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions { BusStatus = "Inactive" });

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-11-06");

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("BUS_INACTIVE", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(seeded.BusId, problem.RootElement.GetProperty("busId").GetGuid());
    }

    [Fact]
    public async Task CreateScheduleTrips_InactiveRoute_StillGeneratesTheTrips()
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions { RouteStatus = "Inactive" });

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-11-06");

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var extension = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(7, extension.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task CreateScheduleTrips_BusOnAnotherTripAtTheTime_IsRefusedBusTripOverlapAndNothingChanges()
    {
        // Arrange: another schedule's trip on 2026-10-26 at 22:00 IST.
        Seeded seeded = await SeedScheduleAsync(new SeedOptions());
        var otherTripId = Guid.NewGuid();
        var departureAt = new DateTimeOffset(2026, 10, 26, 16, 30, 0, TimeSpan.Zero);
        await _database.SeedAsync(InsertOtherTripSql, new
        {
            OtherScheduleId = Guid.NewGuid(),
            TripId = otherTripId,
            OperatorId = seeded.OperatorId.ToString(),
            seeded.RouteId,
            seeded.BusId,
            DepartureAt = departureAt,
        });

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-11-06");

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement conflict = problem.RootElement.GetProperty("conflicts")[0];
        Assert.Equal("BUS_TRIP_OVERLAP", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("2026-10-26", conflict.GetProperty("serviceDate").GetString());
        Assert.Equal(otherTripId, conflict.GetProperty("tripId").GetGuid());
        Assert.Equal(departureAt, conflict.GetProperty("departureAt").GetDateTimeOffset());
        Assert.Equal("2026-10-22", await GeneratedThroughAsync(seeded.ScheduleId));
        Assert.Equal(_firstFortnightText, await ScheduledDatesAsync(seeded.ScheduleId));
    }

    [Theory]
    [InlineData("Lower berth", "[\"Upper berth\"]", "[]")]
    [InlineData("Lower berth,Upper berth,Window", "[]", "[\"Window\"]")]
    public async Task CreateScheduleTrips_FaresNoLongerMatchTheBus_IsRefusedClassFaresMismatch(
        string fareClasses, string missingClasses, string unknownClasses)
    {
        // Arrange: the bus's seat classes were replaced after the schedule's fares were set.
        Seeded seeded = await SeedScheduleAsync(new SeedOptions { FareClasses = fareClasses.Split(',') });

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-11-06");

        // Assert
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CLASS_FARES_MISMATCH", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(missingClasses, problem.RootElement.GetProperty("missingClasses").GetRawText());
        Assert.Equal(unknownClasses, problem.RootElement.GetProperty("unknownClasses").GetRawText());
        Assert.Equal("2026-10-22", await GeneratedThroughAsync(seeded.ScheduleId));
    }

    [Fact]
    public async Task CreateScheduleTrips_BusRenamedBefore_NewTripsCarryTheNewName()
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions());
        await _database.SeedAsync(UpdateBusNameSql, new { seeded.BusId });

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-11-06");

        // Assert
        using var extension = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Shivneri Sleeper", extension.RootElement.GetProperty("items")[0].GetProperty("busName").GetString());
        Assert.Equal("Shivneri Sleeper", await _database.QueryScalarAsync<string>(EventBusNameSql, new { seeded.ScheduleId }));
    }

    [Fact]
    public async Task CreateScheduleTrips_ScheduleActivatedAgain_CanBeExtended()
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions { ScheduleStatus = "Inactive" });
        using HttpResponseMessage activation = await SendAsync(seeded.Token, $"/schedules/{seeded.ScheduleId}/activate", null);

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, "2026-11-06");

        // Assert
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateScheduleTrips_UnknownSchedule_IsRefusedNotFound()
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, Guid.NewGuid(), "2026-11-06");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CreateScheduleTrips_AnotherOperatorsSchedule_IsReportedNotFoundAndLeftAlone()
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions());
        Guid otherOperator = await TestOperators.SeedProfileAsync(_database);

        // Act
        using HttpResponseMessage response = await ExtendAsync(
            TestAccessTokens.CreateOperator(otherOperator), seeded.ScheduleId, "2026-11-06");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("NOT_FOUND", await ProblemCodeAsync(response));
        Assert.Equal("2026-10-22", await GeneratedThroughAsync(seeded.ScheduleId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2026-13-01")]
    public async Task CreateScheduleTrips_MissingOrInvalidEndDate_IsRefusedNamingTheEndDate(string? endDate)
    {
        // Arrange
        Seeded seeded = await SeedScheduleAsync(new SeedOptions());

        // Act
        using HttpResponseMessage response = await ExtendAsync(seeded.Token, seeded.ScheduleId, endDate);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("VALIDATION_FAILED", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("endDate", out _));
    }

    [Fact]
    public async Task CreateScheduleTrips_OperatorWithoutProfile_IsRefusedProfileNotReady()
    {
        // Arrange
        string accessToken = TestAccessTokens.CreateOperator(Guid.NewGuid());

        // Act
        using HttpResponseMessage response = await ExtendAsync(accessToken, Guid.NewGuid(), "2026-11-06");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("OPERATOR_PROFILE_NOT_READY", await ProblemCodeAsync(response));
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Customer")]
    public async Task CreateScheduleTrips_NotAnOperator_IsRefusedForbidden(string role)
    {
        // Arrange
        string accessToken = TestAccessTokens.Create(role);

        // Act
        using HttpResponseMessage response = await ExtendAsync(accessToken, Guid.NewGuid(), "2026-11-06");

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("FORBIDDEN", await ProblemCodeAsync(response));
    }

    [Fact]
    public async Task CreateScheduleTrips_WithoutAccessToken_IsRefusedUnauthorized()
    {
        // Arrange
        using ApiFactory factory = FleetTestHost.For(_database);
        using HttpClient client = factory.CreateClient();
        using var content = new StringContent("{\"endDate\":\"2026-11-06\"}", System.Text.Encoding.UTF8, "application/json");

        // Act
        using HttpResponseMessage response = await client.PostAsync($"/schedules/{Guid.NewGuid()}/trips", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("UNAUTHORIZED", await ProblemCodeAsync(response));
    }

    private static readonly string _firstFortnightText = string.Join(",", _firstFortnight.Select(date => date.ToString("yyyy-MM-dd")));

    private static string ServiceDates(JsonElement extension)
    {
        IEnumerable<string?> dates = extension.GetProperty("items").EnumerateArray().Select(trip => trip.GetProperty("serviceDate").GetString());
        return string.Join(",", dates);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }

    private async Task<Seeded> SeedScheduleAsync(SeedOptions options)
    {
        Guid operatorId = await TestOperators.SeedProfileAsync(_database);
        var seeded = new Seeded(operatorId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await _database.SeedAsync(InsertScheduleSql, new
        {
            seeded.ScheduleId,
            seeded.BusId,
            seeded.RouteId,
            OperatorId = operatorId.ToString(),
            RegistrationNumber = "TX" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            options.BusStatus,
            options.RouteStatus,
            options.ScheduleStatus,
            Mumbai = _mumbai,
            Pune = _pune,
            options.FareClasses,
            FarePaise = options.FareClasses.Select(FarePaise).ToArray(),
        });
        if (options.HasFirstFortnight)
        {
            await SeedTripsAsync(seeded, _firstFortnight, null);
        }

        return seeded;
    }

    private static long FarePaise(string seatClass)
    {
        if (seatClass == "Lower berth")
        {
            return 120000;
        }

        return 100000;
    }

    private async Task SeedTripsAsync(Seeded seeded, DateOnly[] serviceDates, string? cause)
    {
        await _database.SeedAsync(InsertTripsSql, new
        {
            seeded.ScheduleId,
            OperatorId = seeded.OperatorId.ToString(),
            seeded.RouteId,
            seeded.BusId,
            ServiceDates = serviceDates,
            Cause = cause,
        });
    }

    private async Task<string?> GeneratedThroughAsync(Guid scheduleId)
    {
        return await _database.QueryScalarAsync<string>(GeneratedThroughSql, new { ScheduleId = scheduleId });
    }

    private async Task<string?> ScheduledDatesAsync(Guid scheduleId)
    {
        return await _database.QueryScalarAsync<string>(ScheduledDatesSql, new { ScheduleId = scheduleId });
    }

    private async Task<HttpResponseMessage> ExtendAsync(string accessToken, Guid scheduleId, string? endDate)
    {
        var body = new { EndDate = endDate };
        return await SendAsync(accessToken, $"/schedules/{scheduleId}/trips", body);
    }

    private async Task<HttpResponseMessage> SendAsync(string accessToken, string route, object? body)
    {
        using ApiFactory factory = FleetTestHost.For(_database, FixedClock.Override(_now));
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = TestAccessTokens.Authorized(HttpMethod.Post, route, accessToken, body);
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, "extend-test-correlation");
        return await client.SendAsync(request);
    }

    /// <summary>The operator, schedule, bus and route a test extends.</summary>
    private sealed record Seeded(Guid OperatorId, Guid ScheduleId, Guid BusId, Guid RouteId)
    {
        public string Token
        {
            get { return TestAccessTokens.CreateOperator(OperatorId); }
        }
    }

    /// <summary>How to seed schedule S1; by default everything active, fares matching the bus, its first fortnight of trips present.</summary>
    private sealed class SeedOptions
    {
        public string BusStatus { get; init; } = "Active";

        public string RouteStatus { get; init; } = "Active";

        public string ScheduleStatus { get; init; } = "Active";

        public string[] FareClasses { get; init; } = new[] { "Lower berth", "Upper berth" };

        public bool HasFirstFortnight { get; init; } = true;
    }
}
