namespace RouteGo.Fleet.Api.Features.Trips.ReplaceTripFares;

public sealed partial class ReplaceTripFaresHandler
{
    private const string AuditAction = "ReplaceTripFares";

    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Ownership is part of the lookup: another operator's trip is not found, exactly like one that
    // does not exist. The trip's schedule is locked first — the order every request that changes a
    // schedule's trips takes — so an edit, extension or deactivation of the schedule waits its turn.
    private const string LockScheduleSql = """
        SELECT s.schedule_id
        FROM trips.schedules s
        WHERE s.schedule_id = (SELECT t.schedule_id FROM trips.trips t WHERE t.trip_id = @TripId AND t.operator_id = @OperatorId)
        FOR UPDATE
        """;

    private const string SelectTripForUpdateSql = """
        SELECT bus_id, departure_at, status
        FROM trips.trips
        WHERE trip_id = @TripId
        FOR UPDATE
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;

    public ReplaceTripFaresHandler(IWriterConnectionFactory writerFactory, IClock clock)
    {
        _writerFactory = writerFactory;
        _clock = clock;
    }

    /// <summary>
    /// Replaces one of the operator's trips' class fares and records a FareChanged event when any fare
    /// changed, in one transaction — provided the operator has its Fleet profile, the trip is scheduled,
    /// departs more than the fare-change cutoff from now, and the fares name exactly its bus's seat classes.
    /// </summary>
    public async Task<ReplaceTripFaresResult> HandleAsync(
        TripTarget target, ReplaceTripFaresRequest request, CancellationToken cancellationToken)
    {
        List<RequestedFare> fares = Normalize(request);

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { target.OperatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return Refused(ReplaceTripFaresOutcome.OperatorProfileNotReady);
        }

        var ownedTrip = new { target.TripId, target.OperatorId };
        Guid? scheduleId = await QuerySingleOrDefaultAsync<Guid?>(writer, LockScheduleSql, ownedTrip, cancellationToken);
        if (scheduleId is null)
        {
            return Refused(ReplaceTripFaresOutcome.NotFound);
        }

        var tripLookup = new { target.TripId };
        TripRow trip = await writer.Connection.QuerySingleAsync<TripRow>(new CommandDefinition(
            SelectTripForUpdateSql, tripLookup, writer.Transaction, cancellationToken: cancellationToken));
        DateTimeOffset now = _clock.UtcNow;
        ReplaceTripFaresResult? refusal = CheckTrip(trip, now);
        if (refusal != null)
        {
            return refusal;
        }

        var repricing = new Repricing { Target = target, Trip = trip, Fares = fares, Now = now };
        return await RepriceAsync(writer, repricing, cancellationToken);
    }

    private static async Task<ReplaceTripFaresResult> RepriceAsync(
        IWriterConnection writer, Repricing repricing, CancellationToken cancellationToken)
    {
        StoredFares stored = await GetStoredFaresAsync(writer, repricing, cancellationToken);
        FareMatch fares = MatchFares(stored.SeatClasses, repricing.Fares);
        if (fares.MissingClasses.Count > 0 || fares.UnknownClasses.Count > 0)
        {
            return new ReplaceTripFaresResult
            {
                Outcome = ReplaceTripFaresOutcome.ClassFaresMismatch,
                MissingClasses = fares.MissingClasses,
                UnknownClasses = fares.UnknownClasses,
            };
        }

        // Fares equal to the current ones change nothing and announce nothing.
        bool hasNewFares = !HasFares(stored.TripFares, fares.ClassFares);
        if (hasNewFares)
        {
            var fareChange = new FareChange(stored.Seats, fares.ClassFares);
            await UpdateFaresAsync(writer, repricing, fareChange, cancellationToken);
            await InsertFareChangedEventAsync(writer, repricing, fareChange, cancellationToken);
        }

        Trip response = await GetTripAsync(writer, repricing.Target.TripId, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new ReplaceTripFaresResult { Outcome = ReplaceTripFaresOutcome.Replaced, Response = response };
    }

    // A cancelled trip is refused before a departed one; a trip inside the cutoff after both.
    private static ReplaceTripFaresResult? CheckTrip(TripRow trip, DateTimeOffset now)
    {
        if (trip.Status == TripStatus.Cancelled)
        {
            return Refused(ReplaceTripFaresOutcome.TripNotScheduled);
        }

        DateTimeOffset departureAt = ToUtc(trip.DepartureAt);
        if (departureAt <= now)
        {
            return new ReplaceTripFaresResult { Outcome = ReplaceTripFaresOutcome.TripDeparted, DepartedAt = departureAt };
        }

        if (!TripRules.CanChangeFares(departureAt, now))
        {
            DateTimeOffset cutoffAt = TripRules.GetFareChangeCutoffAt(departureAt);
            return new ReplaceTripFaresResult { Outcome = ReplaceTripFaresOutcome.FareChangeCutoffPassed, CutoffAt = cutoffAt };
        }

        return null;
    }

    // Seat class names are compared trimmed.
    private static List<RequestedFare> Normalize(ReplaceTripFaresRequest request)
    {
        var fares = new List<RequestedFare>();
        foreach (ClassFareInput classFare in request.ClassFares!)
        {
            fares.Add(new RequestedFare(classFare.SeatClass!.Trim(), (int)classFare.Fare!.Value));
        }

        return fares;
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

    // A class the trip has no fare for counts as a different fare.
    private static bool HasFares(IReadOnlyList<FareRow> tripFares, IReadOnlyList<SeatClassFare> classFares)
    {
        foreach (SeatClassFare classFare in classFares)
        {
            FareRow? current = tripFares.FirstOrDefault(fare => fare.SeatClass == classFare.SeatClass);
            long paise = classFare.Fare * TripRules.PaisePerRupee;
            if (current is null || current.Fare != paise)
            {
                return false;
            }
        }

        return true;
    }

    private static ReplaceTripFaresResult Refused(ReplaceTripFaresOutcome outcome)
    {
        return new ReplaceTripFaresResult { Outcome = outcome };
    }

    private static async Task<T?> QuerySingleOrDefaultAsync<T>(
        IWriterConnection writer, string sql, object parameters, CancellationToken cancellationToken)
    {
        return await writer.Connection.QuerySingleOrDefaultAsync<T>(new CommandDefinition(
            sql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private sealed record RequestedFare(string SeatClass, int Fare);

    private sealed class FareMatch
    {
        public List<SeatClassFare> ClassFares { get; } = new List<SeatClassFare>();

        public List<string> MissingClasses { get; } = new List<string>();

        public List<string> UnknownClasses { get; } = new List<string>();
    }

    /// <summary>The request as checked so far: which trip, its locked row, the fares asked for, and the instant.</summary>
    private sealed class Repricing
    {
        public required TripTarget Target { get; init; }

        public required TripRow Trip { get; init; }

        public required IReadOnlyList<RequestedFare> Fares { get; init; }

        public required DateTimeOffset Now { get; init; }
    }

    /// <summary>What the FareChanged event announces: every seat of the bus, and the new fare of every class.</summary>
    /// <param name="Seats">Every seat of the trip's bus.</param>
    /// <param name="ClassFares">The new fare of every class, in the bus's seat-class order.</param>
    private sealed record FareChange(IReadOnlyList<SnapshotSeat> Seats, IReadOnlyList<SeatClassFare> ClassFares);

    private sealed class TripRow
    {
        public Guid BusId { get; init; }

        public DateTime DepartureAt { get; init; }

        /// <summary>Scheduled or Cancelled, as stored.</summary>
        public TripStatus Status { get; init; }
    }
}
