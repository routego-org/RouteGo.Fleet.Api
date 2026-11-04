namespace RouteGo.Fleet.Api.Features.Trips.CreateScheduleTrips;

public sealed partial class CreateScheduleTripsHandler
{
    private const string AuditAction = "CreateScheduleTrips";

    // The operator's details travel in every trip snapshot, so the profile gate reads them too.
    private const string SelectOperatorSql = """
        SELECT company_name, email, rating
        FROM operator_profiles.operator_profiles
        WHERE operator_id = @OperatorId
        """;

    // Ownership is part of the lookup: another operator's schedule is not found, exactly like one
    // that does not exist. The row lock keeps a concurrent extension, edit or deactivation of the
    // same schedule waiting until this one commits.
    private const string SelectScheduleForUpdateSql = """
        SELECT route_id, bus_id, departure_time, days_of_week, status, trips_generated_through
        FROM trips.schedules
        WHERE schedule_id = @ScheduleId AND operator_id = @OperatorId
        FOR UPDATE
        """;

    // Locked after the schedule (always in that order): every request that gives this bus trips,
    // or deactivates or reshapes it, waits its turn, so the overlap check cannot race another.
    private const string SelectBusForUpdateSql = """
        SELECT name, bus_type, is_ac, amenities, status
        FROM buses.buses
        WHERE bus_id = @BusId
        FOR UPDATE
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;

    public CreateScheduleTripsHandler(IWriterConnectionFactory writerFactory, IClock clock)
    {
        _writerFactory = writerFactory;
        _clock = clock;
    }

    /// <summary>
    /// Generates the schedule's missing trips from today through the end date, with a TripScheduled
    /// event for each, and moves its generated-through date forward, all in one transaction —
    /// provided the operator has its Fleet profile, the schedule is its own and active, the bus is
    /// active, the end date is in range, the fares still match the bus and no trip would overlap.
    /// </summary>
    public async Task<CreateScheduleTripsResult> HandleAsync(
        ExtensionTarget target, DateOnly endDate, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        var owner = new { target.OperatorId };
        OperatorRow? profile = await QuerySingleOrDefaultAsync<OperatorRow>(writer, SelectOperatorSql, owner, cancellationToken);
        if (profile is null)
        {
            return Refused(CreateScheduleTripsOutcome.OperatorProfileNotReady);
        }

        var ownedSchedule = new { target.ScheduleId, target.OperatorId };
        ScheduleRow? schedule = await QuerySingleOrDefaultAsync<ScheduleRow>(writer, SelectScheduleForUpdateSql, ownedSchedule, cancellationToken);
        if (schedule is null)
        {
            return Refused(CreateScheduleTripsOutcome.NotFound);
        }

        if (schedule.Status != ScheduleStatus.Active)
        {
            return Refused(CreateScheduleTripsOutcome.ScheduleInactive);
        }

        var extension = new Extension { Target = target, EndDate = endDate, Now = _clock.UtcNow, Profile = profile, Schedule = schedule };
        return await ExtendOnBusAsync(writer, extension, cancellationToken);
    }

    private static async Task<CreateScheduleTripsResult> ExtendOnBusAsync(
        IWriterConnection writer, Extension extension, CancellationToken cancellationToken)
    {
        var busLookup = new { extension.Schedule.BusId };
        BusRow bus = await writer.Connection.QuerySingleAsync<BusRow>(new CommandDefinition(
            SelectBusForUpdateSql, busLookup, writer.Transaction, cancellationToken: cancellationToken));
        if (bus.Status != BusStatus.Active)
        {
            return new CreateScheduleTripsResult { Outcome = CreateScheduleTripsOutcome.BusInactive, BusId = extension.Schedule.BusId };
        }

        DateOnly today = TripRules.GetServiceDate(extension.Now);
        if (!TripRules.IsAcceptableEndDate(extension.EndDate, today))
        {
            DateOnly latest = TripRules.GetLatestEndDate(today);
            return new CreateScheduleTripsResult { Outcome = CreateScheduleTripsOutcome.EndDateOutOfRange, LatestAllowedDate = latest };
        }

        return await ExtendWithFaresAsync(writer, extension, bus, cancellationToken);
    }

    private static async Task<CreateScheduleTripsResult> ExtendWithFaresAsync(
        IWriterConnection writer, Extension extension, BusRow bus, CancellationToken cancellationToken)
    {
        StoredParts parts = await GetPartsAsync(writer, extension, cancellationToken);
        FareMatch fares = MatchFares(parts.SeatClasses, parts.Fares);
        if (fares.MissingClasses.Count > 0 || fares.UnknownClasses.Count > 0)
        {
            return new CreateScheduleTripsResult
            {
                Outcome = CreateScheduleTripsOutcome.ClassFaresMismatch,
                MissingClasses = fares.MissingClasses,
                UnknownClasses = fares.UnknownClasses,
            };
        }

        var source = new ExtensionSource { Extension = extension, Bus = bus, Parts = parts, ClassFares = fares.ClassFares };
        return await GenerateAsync(writer, source, cancellationToken);
    }

    private static async Task<CreateScheduleTripsResult> GenerateAsync(
        IWriterConnection writer, ExtensionSource source, CancellationToken cancellationToken)
    {
        Extension extension = source.Extension;
        List<PlannedTrip> trips = TripPlan.GenerateTrips(extension.Now, ToTimetable(source), extension.EndDate, source.Parts.TakenDates);
        if (trips.Count > 0)
        {
            List<BusyTrip> busTrips = await ListBusTripsAsync(writer, extension.Schedule.BusId, trips, cancellationToken);
            List<TripConflict> conflicts = TripPlan.FindConflicts(trips, busTrips);
            if (conflicts.Count > 0)
            {
                return new CreateScheduleTripsResult { Outcome = CreateScheduleTripsOutcome.BusTripOverlap, Conflicts = conflicts };
            }
        }

        DateOnly generatedThrough = await InsertAsync(writer, source, trips, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        var response = new CreateScheduleTripsResponse
        {
            ScheduleId = extension.Target.ScheduleId,
            TripsGeneratedThrough = generatedThrough,
            Items = ToTrips(source, trips),
        };
        return new CreateScheduleTripsResult { Outcome = CreateScheduleTripsOutcome.Created, Response = response };
    }

    private static Timetable ToTimetable(ExtensionSource source)
    {
        ScheduleRow schedule = source.Extension.Schedule;
        var daysOfWeek = new List<DayOfWeek>();
        foreach (string day in schedule.DaysOfWeek)
        {
            daysOfWeek.Add(Enum.Parse<DayOfWeek>(day));
        }

        var journeyTime = TimeSpan.FromMinutes(source.Parts.Stops[^1].OffsetMinutes);
        return new Timetable(TripRules.ParseDepartureTime(schedule.DepartureTime), daysOfWeek, journeyTime);
    }

    // The schedule's fares were stored in the bus's spelling, so they match the bus's classes exactly
    // unless the bus's seat classes were replaced since; each side's leftovers are reported.
    private static FareMatch MatchFares(IReadOnlyList<string> seatClasses, IReadOnlyList<StoredFare> fares)
    {
        var match = new FareMatch();
        foreach (string seatClass in seatClasses)
        {
            StoredFare? fare = fares.FirstOrDefault(stored => stored.SeatClass == seatClass);
            if (fare is null)
            {
                match.MissingClasses.Add(seatClass);
                continue;
            }

            match.ClassFares.Add(new SeatClassFare(seatClass, fare.Rupees));
        }

        foreach (StoredFare fare in fares)
        {
            if (!seatClasses.Contains(fare.SeatClass))
            {
                match.UnknownClasses.Add(fare.SeatClass);
            }
        }

        return match;
    }

    private static CreateScheduleTripsResult Refused(CreateScheduleTripsOutcome outcome)
    {
        return new CreateScheduleTripsResult { Outcome = outcome };
    }

    private static async Task<T?> QuerySingleOrDefaultAsync<T>(
        IWriterConnection writer, string sql, object parameters, CancellationToken cancellationToken)
    {
        return await writer.Connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(
            sql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    /// <summary>The request as checked so far: what to extend, to when, at what instant, and the rows read.</summary>
    private sealed class Extension
    {
        public required ExtensionTarget Target { get; init; }

        public required DateOnly EndDate { get; init; }

        public required DateTimeOffset Now { get; init; }

        public required OperatorRow Profile { get; init; }

        public required ScheduleRow Schedule { get; init; }
    }

    /// <summary>Everything the new trips, their fares, events and the response are built from.</summary>
    private sealed class ExtensionSource
    {
        public required Extension Extension { get; init; }

        public required BusRow Bus { get; init; }

        public required StoredParts Parts { get; init; }

        /// <summary>One fare per seat class of the bus, in the bus's seat-class order.</summary>
        public required IReadOnlyList<SeatClassFare> ClassFares { get; init; }
    }

    private sealed class FareMatch
    {
        public List<SeatClassFare> ClassFares { get; } = new List<SeatClassFare>();

        public List<string> MissingClasses { get; } = new List<string>();

        public List<string> UnknownClasses { get; } = new List<string>();
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

        public Guid BusId { get; init; }

        public string DepartureTime { get; init; } = string.Empty;

        public string[] DaysOfWeek { get; init; } = Array.Empty<string>();

        public ScheduleStatus Status { get; init; }

        public DateTime TripsGeneratedThrough { get; init; }
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
