namespace RouteGo.Fleet.Api.Features.Trips.CreateSchedule;

public sealed partial class CreateScheduleHandler
{
    private const string AuditAction = "CreateSchedule";

    // The operator's details travel in every trip snapshot, so the profile gate reads them too.
    private const string SelectOperatorSql = """
        SELECT company_name, email, rating
        FROM operator_profiles.operator_profiles
        WHERE operator_id = @OperatorId
        """;

    // Ownership is part of the lookup: another operator's route is unknown, exactly like one that
    // does not exist. The shared lock keeps the route from being deactivated before this commits.
    private const string SelectRouteStatusSql = """
        SELECT status
        FROM routes.routes
        WHERE route_id = @RouteId AND operator_id = @OperatorId
        FOR SHARE
        """;

    // The row lock makes every request that gives this bus trips, or deactivates or reshapes it,
    // wait its turn, so the overlap check below cannot race another schedule for the same bus.
    private const string SelectBusForUpdateSql = """
        SELECT name, bus_type, is_ac, amenities, status
        FROM buses.buses
        WHERE bus_id = @BusId AND operator_id = @OperatorId
        FOR UPDATE
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;

    public CreateScheduleHandler(IWriterConnectionFactory writerFactory, IClock clock)
    {
        _writerFactory = writerFactory;
        _clock = clock;
    }

    /// <summary>
    /// Creates the active schedule, its class fares, its trips for the next 14 days and a
    /// TripScheduled event for each trip, all in one transaction — provided the operator has its
    /// Fleet profile, the route and bus are its own and active, the fares name exactly the bus's
    /// seat classes, and no trip would overlap another trip of the bus.
    /// </summary>
    public async Task<CreateScheduleResult> HandleAsync(
        CreateScheduleRequest request, string operatorId, string correlationId, CancellationToken cancellationToken)
    {
        NewSchedule schedule = Normalize(request, operatorId, correlationId);

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        var owner = new { OperatorId = operatorId };
        OperatorRow? profile = await QuerySingleOrDefaultAsync<OperatorRow>(writer, SelectOperatorSql, owner, cancellationToken);
        if (profile is null)
        {
            return Refused(CreateScheduleOutcome.OperatorProfileNotReady);
        }

        CreateScheduleOutcome? refusal = await CheckRouteAsync(writer, schedule, cancellationToken);
        if (refusal != null)
        {
            return Refused(refusal.Value);
        }

        var ownedBus = new { schedule.BusId, OperatorId = operatorId };
        BusRow? bus = await QuerySingleOrDefaultAsync<BusRow>(writer, SelectBusForUpdateSql, ownedBus, cancellationToken);
        refusal = CheckBus(bus);
        if (refusal != null)
        {
            return Refused(refusal.Value);
        }

        return await CreateOnBusAsync(writer, schedule, new ScheduleOwner(profile, bus!), cancellationToken);
    }

    private async Task<CreateScheduleResult> CreateOnBusAsync(
        IWriterConnection writer, NewSchedule schedule, ScheduleOwner owner, CancellationToken cancellationToken)
    {
        BusLayout layout = await GetBusLayoutAsync(writer, schedule.BusId, cancellationToken);
        FareMatch fares = MatchFares(layout.SeatClasses, schedule.Fares);
        if (fares.MissingClasses.Count > 0 || fares.UnknownClasses.Count > 0)
        {
            return new CreateScheduleResult
            {
                Outcome = CreateScheduleOutcome.ClassFaresMismatch,
                MissingClasses = fares.MissingClasses,
                UnknownClasses = fares.UnknownClasses,
            };
        }

        List<StopRow> stops = await ListStopsAsync(writer, schedule.RouteId, cancellationToken);
        var source = new ScheduleSource { Owner = owner, Layout = layout, Stops = stops, ClassFares = fares.ClassFares };
        return await GenerateAsync(writer, schedule, source, cancellationToken);
    }

    private async Task<CreateScheduleResult> GenerateAsync(
        IWriterConnection writer, NewSchedule schedule, ScheduleSource source, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        var journeyTime = TimeSpan.FromMinutes(source.Stops[^1].OffsetMinutes);
        var timetable = new Timetable(schedule.Departure, schedule.DaysOfWeek, journeyTime);
        DateOnly today = TripRules.GetServiceDate(now);
        DateOnly generatedThrough = TripRules.GetInitialGeneratedThrough(today);

        // A new schedule has no trips yet, so no date is taken.
        List<PlannedTrip> trips = TripPlan.GenerateTrips(now, timetable, generatedThrough, new HashSet<DateOnly>());
        List<BusyTrip> busTrips = await ListBusTripsAsync(writer, schedule.BusId, trips, cancellationToken);
        List<TripConflict> conflicts = TripPlan.FindConflicts(trips, busTrips);
        if (conflicts.Count > 0)
        {
            return new CreateScheduleResult { Outcome = CreateScheduleOutcome.BusTripOverlap, Conflicts = conflicts };
        }

        var generation = new Generation
        {
            Source = source,
            Trips = trips,
            GeneratedThrough = generatedThrough,
            RecordedAt = now,
        };
        CreateScheduleResponse response = await InsertAsync(writer, schedule, generation, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new CreateScheduleResult { Outcome = CreateScheduleOutcome.Created, Response = response };
    }

    // Seat class names are compared trimmed; days are kept once each, in week order.
    private static NewSchedule Normalize(CreateScheduleRequest request, string operatorId, string correlationId)
    {
        var fares = new List<RequestedFare>();
        foreach (ClassFareInput classFare in request.ClassFares!)
        {
            fares.Add(new RequestedFare(classFare.SeatClass!.Trim(), (int)classFare.Fare!.Value));
        }

        return new NewSchedule
        {
            ScheduleId = Guid.NewGuid(),
            OperatorId = operatorId,
            CorrelationId = correlationId,
            RouteId = request.RouteId!.Value,
            BusId = request.BusId!.Value,
            DepartureTime = request.DepartureTime!,
            Departure = TripRules.ParseDepartureTime(request.DepartureTime!),
            DaysOfWeek = TripRules.InWeekOrder(request.DaysOfWeek!),
            Fares = fares,
        };
    }

    private static async Task<CreateScheduleOutcome?> CheckRouteAsync(
        IWriterConnection writer, NewSchedule schedule, CancellationToken cancellationToken)
    {
        var ownedRoute = new { schedule.RouteId, schedule.OperatorId };
        RouteRow? route = await QuerySingleOrDefaultAsync<RouteRow>(writer, SelectRouteStatusSql, ownedRoute, cancellationToken);
        if (route is null)
        {
            return CreateScheduleOutcome.UnknownRoute;
        }

        if (route.Status != RouteStatus.Active)
        {
            return CreateScheduleOutcome.RouteInactive;
        }

        return null;
    }

    private static CreateScheduleOutcome? CheckBus(BusRow? bus)
    {
        if (bus is null)
        {
            return CreateScheduleOutcome.UnknownBus;
        }

        if (bus.Status != BusStatus.Active)
        {
            return CreateScheduleOutcome.BusInactive;
        }

        return null;
    }

    // Each of the bus's classes takes the fare given for it under any spelling, and keeps the
    // bus's own spelling, so trip fares and seats always name a class identically.
    private static FareMatch MatchFares(IReadOnlyList<string> seatClasses, IReadOnlyList<RequestedFare> requested)
    {
        var match = new FareMatch();
        foreach (string seatClass in seatClasses)
        {
            RequestedFare? fare = requested.FirstOrDefault(given => BusRules.IsSameSeatClass(given.SeatClass, seatClass));
            if (fare is null)
            {
                match.MissingClasses.Add(seatClass);
                continue;
            }

            match.ClassFares.Add(new ClassFare(seatClass, fare.Fare));
        }

        foreach (RequestedFare fare in requested)
        {
            bool isBusClass = seatClasses.Any(seatClass => BusRules.IsSameSeatClass(seatClass, fare.SeatClass));
            if (!isBusClass)
            {
                match.UnknownClasses.Add(fare.SeatClass);
            }
        }

        return match;
    }

    private static CreateScheduleResult Refused(CreateScheduleOutcome outcome)
    {
        return new CreateScheduleResult { Outcome = outcome };
    }

    private static async Task<T?> QuerySingleOrDefaultAsync<T>(
        IWriterConnection writer, string sql, object parameters, CancellationToken cancellationToken)
    {
        return await writer.Connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(
            sql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    /// <summary>The schedule as requested, normalized, with who asked and under which correlation ID.</summary>
    private sealed class NewSchedule
    {
        /// <summary>Assigned up front, so every trip and event can name the schedule before it is written.</summary>
        public required Guid ScheduleId { get; init; }

        public required string OperatorId { get; init; }

        public required string CorrelationId { get; init; }

        public required Guid RouteId { get; init; }

        public required Guid BusId { get; init; }

        public required string DepartureTime { get; init; }

        public required TimeOnly Departure { get; init; }

        public required IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; }

        public required IReadOnlyList<RequestedFare> Fares { get; init; }
    }

    private sealed record RequestedFare(string SeatClass, int Fare);

    private sealed class FareMatch
    {
        public List<ClassFare> ClassFares { get; } = new List<ClassFare>();

        public List<string> MissingClasses { get; } = new List<string>();

        public List<string> UnknownClasses { get; } = new List<string>();
    }

    /// <summary>The operator and bus the schedule's trips are announced with.</summary>
    /// <param name="Operator">The operator's profile.</param>
    /// <param name="Bus">The bus, locked for this request.</param>
    private sealed record ScheduleOwner(OperatorRow Operator, BusRow Bus);

    /// <summary>Everything stored that the new trips and their snapshots are built from.</summary>
    private sealed class ScheduleSource
    {
        public required ScheduleOwner Owner { get; init; }

        public required BusLayout Layout { get; init; }

        public required IReadOnlyList<StopRow> Stops { get; init; }

        /// <summary>One fare per seat class of the bus, in the bus's seat-class order.</summary>
        public required IReadOnlyList<ClassFare> ClassFares { get; init; }
    }

    /// <summary>What generating the trips decided: from what, the trips, how far they reach, and when they were recorded.</summary>
    private sealed class Generation
    {
        public required ScheduleSource Source { get; init; }

        public required IReadOnlyList<PlannedTrip> Trips { get; init; }

        public required DateOnly GeneratedThrough { get; init; }

        public required DateTimeOffset RecordedAt { get; init; }
    }

    private sealed class OperatorRow
    {
        public string CompanyName { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public decimal Rating { get; init; }
    }

    private sealed class RouteRow
    {
        public RouteStatus Status { get; init; }
    }

    private sealed class BusRow
    {
        public string Name { get; init; } = string.Empty;

        public BusType BusType { get; init; }

        public bool IsAc { get; init; }

        public string[]? Amenities { get; init; }

        public BusStatus Status { get; init; }
    }
}
