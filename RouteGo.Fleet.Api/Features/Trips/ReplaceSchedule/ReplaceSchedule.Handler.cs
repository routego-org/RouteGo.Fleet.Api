namespace RouteGo.Fleet.Api.Features.Trips.ReplaceSchedule;

public sealed partial class ReplaceScheduleHandler
{
    private const string AuditAction = "ReplaceSchedule";

    // The operator's details travel in every trip snapshot, so the profile gate reads them too.
    private const string SelectOperatorSql = """
        SELECT company_name, email, rating
        FROM operator_profiles.operator_profiles
        WHERE operator_id = @OperatorId
        """;

    // Ownership is part of the lookup: another operator's schedule is not found, exactly like one
    // that does not exist. The row lock keeps a concurrent edit, extension or deactivation of the
    // same schedule waiting until this one commits.
    private const string SelectScheduleForUpdateSql = """
        SELECT route_id, status, trips_generated_through
        FROM trips.schedules
        WHERE schedule_id = @ScheduleId AND operator_id = @OperatorId
        FOR UPDATE
        """;

    // Locked after the schedule (always in that order): every request that gives this bus trips,
    // or deactivates or reshapes it, waits its turn, so the overlap check cannot race another.
    private const string SelectBusForUpdateSql = """
        SELECT name, bus_type, is_ac, amenities, status
        FROM buses.buses
        WHERE bus_id = @BusId AND operator_id = @OperatorId
        FOR UPDATE
        """;

    // The shared lock keeps the route from being deactivated before this commits.
    private const string SelectRouteStatusSql = """
        SELECT status
        FROM routes.routes
        WHERE route_id = @RouteId AND operator_id = @OperatorId
        FOR SHARE
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;

    public ReplaceScheduleHandler(IWriterConnectionFactory writerFactory, IClock clock)
    {
        _writerFactory = writerFactory;
        _clock = clock;
    }

    /// <summary>
    /// Replaces one of the operator's active schedules and rewrites its future trips to match —
    /// cancelling, generating and re-pricing them with an event for each — all in one transaction,
    /// provided the operator has its Fleet profile, the bus is its own and active, a changed route is
    /// its own and active, the fares name exactly the bus's seat classes, and no trip would overlap.
    /// </summary>
    public async Task<ReplaceScheduleResult> HandleAsync(
        ScheduleTarget target, ReplaceScheduleRequest request, CancellationToken cancellationToken)
    {
        NewDetails details = Normalize(request);

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        var owner = new { target.OperatorId };
        OperatorRow? profile = await QuerySingleOrDefaultAsync<OperatorRow>(writer, SelectOperatorSql, owner, cancellationToken);
        if (profile is null)
        {
            return Refused(ReplaceScheduleOutcome.OperatorProfileNotReady);
        }

        var ownedSchedule = new { target.ScheduleId, target.OperatorId };
        ScheduleRow? schedule = await QuerySingleOrDefaultAsync<ScheduleRow>(writer, SelectScheduleForUpdateSql, ownedSchedule, cancellationToken);
        if (schedule is null)
        {
            return Refused(ReplaceScheduleOutcome.NotFound);
        }

        if (schedule.Status != ScheduleStatus.Active)
        {
            return Refused(ReplaceScheduleOutcome.ScheduleInactive);
        }

        var replacement = new Replacement { Target = target, Details = details, Profile = profile, Schedule = schedule, Now = _clock.UtcNow };
        return await ReplaceOnBusAsync(writer, replacement, cancellationToken);
    }

    private static async Task<ReplaceScheduleResult> ReplaceOnBusAsync(
        IWriterConnection writer, Replacement replacement, CancellationToken cancellationToken)
    {
        var ownedBus = new { replacement.Details.BusId, replacement.Target.OperatorId };
        BusRow? bus = await QuerySingleOrDefaultAsync<BusRow>(writer, SelectBusForUpdateSql, ownedBus, cancellationToken);
        ReplaceScheduleOutcome? refusal = CheckBus(bus);
        if (refusal != null)
        {
            return Refused(refusal.Value);
        }

        refusal = await CheckRouteAsync(writer, replacement, cancellationToken);
        if (refusal != null)
        {
            return Refused(refusal.Value);
        }

        return await ReplaceWithFaresAsync(writer, replacement, bus!, cancellationToken);
    }

    private static async Task<ReplaceScheduleResult> ReplaceWithFaresAsync(
        IWriterConnection writer, Replacement replacement, BusRow bus, CancellationToken cancellationToken)
    {
        BusLayout layout = await GetBusLayoutAsync(writer, replacement.Details.BusId, cancellationToken);
        FareMatch fares = MatchFares(layout.SeatClasses, replacement.Details.Fares);
        if (fares.MissingClasses.Count > 0 || fares.UnknownClasses.Count > 0)
        {
            return new ReplaceScheduleResult
            {
                Outcome = ReplaceScheduleOutcome.ClassFaresMismatch,
                MissingClasses = fares.MissingClasses,
                UnknownClasses = fares.UnknownClasses,
            };
        }

        List<StopRow> stops = await ListStopsAsync(writer, replacement.Details.RouteId, cancellationToken);
        var source = new ReplacementSource
        {
            Replacement = replacement,
            Bus = bus,
            Layout = layout,
            Stops = stops,
            ClassFares = fares.ClassFares,
        };
        return await RewriteAsync(writer, source, cancellationToken);
    }

    // Seat class names are compared trimmed; days are kept once each, in week order.
    private static NewDetails Normalize(ReplaceScheduleRequest request)
    {
        var fares = new List<RequestedFare>();
        foreach (ClassFareInput classFare in request.ClassFares!)
        {
            fares.Add(new RequestedFare(classFare.SeatClass!.Trim(), (int)classFare.Fare!.Value));
        }

        return new NewDetails
        {
            RouteId = request.RouteId!.Value,
            BusId = request.BusId!.Value,
            DepartureTime = request.DepartureTime!,
            Departure = TripRules.ParseDepartureTime(request.DepartureTime!),
            DaysOfWeek = TripRules.InWeekOrder(request.DaysOfWeek!),
            Fares = fares,
        };
    }

    private static ReplaceScheduleOutcome? CheckBus(BusRow? bus)
    {
        if (bus is null)
        {
            return ReplaceScheduleOutcome.UnknownBus;
        }

        if (bus.Status != BusStatus.Active)
        {
            return ReplaceScheduleOutcome.BusInactive;
        }

        return null;
    }

    // The schedule's own route may have been deactivated since and still be kept; only a move to
    // another route needs that route to be active.
    private static async Task<ReplaceScheduleOutcome?> CheckRouteAsync(
        IWriterConnection writer, Replacement replacement, CancellationToken cancellationToken)
    {
        var ownedRoute = new { replacement.Details.RouteId, replacement.Target.OperatorId };
        RouteRow? route = await QuerySingleOrDefaultAsync<RouteRow>(writer, SelectRouteStatusSql, ownedRoute, cancellationToken);
        if (route is null)
        {
            return ReplaceScheduleOutcome.UnknownRoute;
        }

        bool isRouteChanged = replacement.Details.RouteId != replacement.Schedule.RouteId;
        if (isRouteChanged && route.Status != RouteStatus.Active)
        {
            return ReplaceScheduleOutcome.RouteInactive;
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

            match.ClassFares.Add(new SeatClassFare(seatClass, fare.Fare));
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

    private static ReplaceScheduleResult Refused(ReplaceScheduleOutcome outcome)
    {
        return new ReplaceScheduleResult { Outcome = outcome };
    }

    private static async Task<T?> QuerySingleOrDefaultAsync<T>(
        IWriterConnection writer, string sql, object parameters, CancellationToken cancellationToken)
    {
        return await writer.Connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(
            sql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    /// <summary>The schedule's new details as requested, normalized.</summary>
    private sealed class NewDetails
    {
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
        public List<SeatClassFare> ClassFares { get; } = new List<SeatClassFare>();

        public List<string> MissingClasses { get; } = new List<string>();

        public List<string> UnknownClasses { get; } = new List<string>();
    }

    /// <summary>The request as checked so far: which schedule, its new details, the instant, and the rows read.</summary>
    private sealed class Replacement
    {
        public required ScheduleTarget Target { get; init; }

        public required NewDetails Details { get; init; }

        public required OperatorRow Profile { get; init; }

        public required ScheduleRow Schedule { get; init; }

        public required DateTimeOffset Now { get; init; }
    }

    /// <summary>Everything the rewritten trips, their fares and events are built from.</summary>
    private sealed class ReplacementSource
    {
        public required Replacement Replacement { get; init; }

        /// <summary>The new bus, locked for this request.</summary>
        public required BusRow Bus { get; init; }

        public required BusLayout Layout { get; init; }

        /// <summary>The new route's stops, in order.</summary>
        public required IReadOnlyList<StopRow> Stops { get; init; }

        /// <summary>One fare per seat class of the bus, in the bus's seat-class order.</summary>
        public required IReadOnlyList<SeatClassFare> ClassFares { get; init; }
    }

    private sealed class OperatorRow
    {
        public string CompanyName { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public decimal Rating { get; init; }
    }

    private sealed class ScheduleRow
    {
        public Guid RouteId { get; init; }

        public ScheduleStatus Status { get; init; }

        public DateTime TripsGeneratedThrough { get; init; }
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
