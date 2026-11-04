namespace RouteGo.Fleet.Api.Features.Trips.CancelTrip;

public sealed partial class CancelTripHandler
{
    private const string AuditAction = "CancelTrip";

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
        SELECT departure_at, status
        FROM trips.trips
        WHERE trip_id = @TripId
        FOR UPDATE
        """;

    // The Operator cause is what keeps the schedule from ever generating a trip on this date again.
    // The cancellation records its TripCancelled event in the same statement.
    private const string CancelTripSql = """
        WITH cancelled AS (
            UPDATE trips.trips
            SET status = 'Cancelled', cancellation_cause = 'Operator', cancel_reason = @Reason, cancelled_at = @Now
            WHERE trip_id = @TripId AND status = 'Scheduled'
            RETURNING trip_id
        )
        INSERT INTO trips.fleet_events (event_id, event_type, trip_id, data, correlation_id, recorded_at)
        SELECT @EventId, 'TripCancelled', trip_id, jsonb_build_object('tripId', trip_id, 'reason', @Reason),
               @CorrelationId, @Now
        FROM cancelled
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;

    public CancelTripHandler(IWriterConnectionFactory writerFactory, IClock clock)
    {
        _writerFactory = writerFactory;
        _clock = clock;
    }

    /// <summary>
    /// Cancels one of the operator's trips with the reason and records a TripCancelled event, in one
    /// transaction — provided the operator has its Fleet profile and the trip is scheduled and has not
    /// departed.
    /// </summary>
    public async Task<CancelTripResult> HandleAsync(TripTarget target, string reason, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { target.OperatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new CancelTripResult { Outcome = CancelTripOutcome.OperatorProfileNotReady };
        }

        var ownedTrip = new { target.TripId, target.OperatorId };
        Guid? scheduleId = await writer.Connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            LockScheduleSql, ownedTrip, writer.Transaction, cancellationToken: cancellationToken));
        if (scheduleId is null)
        {
            return new CancelTripResult { Outcome = CancelTripOutcome.NotFound };
        }

        DateTimeOffset now = _clock.UtcNow;
        CancelTripResult? refusal = await CheckTripAsync(writer, target.TripId, now, cancellationToken);
        if (refusal != null)
        {
            return refusal;
        }

        var cancellation = new Cancellation(target, reason, now);
        await CancelAsync(writer, cancellation, cancellationToken);
        Trip response = await GetTripAsync(writer, target.TripId, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new CancelTripResult { Outcome = CancelTripOutcome.Cancelled, Response = response };
    }

    // An already-cancelled trip is refused before a departed one; there is no cutoff before departure.
    private static async Task<CancelTripResult?> CheckTripAsync(
        IWriterConnection writer, Guid tripId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        TripRow trip = await writer.Connection.QuerySingleAsync<TripRow>(new CommandDefinition(
            SelectTripForUpdateSql, new { TripId = tripId }, writer.Transaction, cancellationToken: cancellationToken));
        if (trip.Status == TripStatus.Cancelled)
        {
            return new CancelTripResult { Outcome = CancelTripOutcome.TripNotScheduled };
        }

        DateTimeOffset departureAt = ToUtc(trip.DepartureAt);
        if (departureAt <= now)
        {
            return new CancelTripResult { Outcome = CancelTripOutcome.TripDeparted, DepartedAt = departureAt };
        }

        return null;
    }

    private static async Task CancelAsync(IWriterConnection writer, Cancellation cancellation, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            cancellation.Target.TripId,
            cancellation.Reason,
            cancellation.Now,
            EventId = Guid.NewGuid(),
            cancellation.Target.CorrelationId,
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            CancelTripSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    /// <summary>Which trip is cancelled, why, and when.</summary>
    /// <param name="Target">The trip and who asked.</param>
    /// <param name="Reason">The operator's reason.</param>
    /// <param name="Now">The cancellation time.</param>
    private sealed record Cancellation(TripTarget Target, string Reason, DateTimeOffset Now);

    private sealed class TripRow
    {
        public DateTime DepartureAt { get; init; }

        /// <summary>Scheduled or Cancelled, as stored.</summary>
        public TripStatus Status { get; init; }
    }
}
