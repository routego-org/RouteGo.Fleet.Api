namespace RouteGo.Fleet.Api.Features.Trips.DeactivateSchedule;

public sealed class DeactivateScheduleHandler
{
    private const string AuditAction = "DeactivateSchedule";
    private const string DeactivatedReason = "Schedule deactivated";

    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Ownership is part of the lookup: another operator's schedule is not found, exactly like one
    // that does not exist. The row lock keeps an edit or extension from adding trips between the
    // cancellation below and the status change.
    private const string SelectStatusForUpdateSql = """
        SELECT status
        FROM trips.schedules
        WHERE schedule_id = @ScheduleId AND operator_id = @OperatorId
        FOR UPDATE
        """;

    private const string DeactivateScheduleSql = """
        UPDATE trips.schedules
        SET status = @Inactive
        WHERE schedule_id = @ScheduleId
        """;

    // Only future scheduled trips are cancelled: departed and already-cancelled ones keep their
    // state. Each cancellation records its TripCancelled event in the same statement.
    private const string CancelFutureTripsSql = """
        WITH cancelled AS (
            UPDATE trips.trips
            SET status = 'Cancelled', cancellation_cause = 'ScheduleDeactivated', cancel_reason = @Reason, cancelled_at = @Now
            WHERE schedule_id = @ScheduleId AND status = 'Scheduled' AND departure_at > @Now
            RETURNING trip_id
        )
        INSERT INTO trips.fleet_events (event_id, event_type, trip_id, data, correlation_id, recorded_at)
        SELECT gen_random_uuid(), 'TripCancelled', trip_id, jsonb_build_object('tripId', trip_id, 'reason', @Reason),
               @CorrelationId, @Now
        FROM cancelled
        """;

    private const string SelectScheduleSql = """
        SELECT schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status,
               trips_generated_through, created_at, updated_at
        FROM trips.schedules
        WHERE schedule_id = @ScheduleId
        """;

    // Fares follow the bus's seat-class order; a class the bus has since dropped goes last, by name.
    private const string SelectFaresSql = """
        SELECT f.seat_class, f.fare
        FROM trips.schedule_class_fares f
        JOIN trips.schedules s ON s.schedule_id = f.schedule_id
        LEFT JOIN buses.seat_classes c ON c.bus_id = s.bus_id AND c.name = f.seat_class
        WHERE f.schedule_id = @ScheduleId
        ORDER BY c.display_order NULLS LAST, f.seat_class
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;

    public DeactivateScheduleHandler(IWriterConnectionFactory writerFactory, IClock clock)
    {
        _writerFactory = writerFactory;
        _clock = clock;
    }

    /// <summary>
    /// Makes one of the operator's schedules inactive and cancels its future trips, recording a
    /// TripCancelled event for each, in one transaction — provided the operator has its Fleet
    /// profile. An inactive schedule is returned unchanged with nothing cancelled.
    /// </summary>
    public async Task<DeactivateScheduleResult> HandleAsync(ScheduleTarget target, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { target.OperatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new DeactivateScheduleResult(DeactivateScheduleOutcome.OperatorProfileNotReady);
        }

        var lookup = new { target.ScheduleId, target.OperatorId };
        LockedSchedule? locked = await writer.Connection.QuerySingleOrDefaultAsync<LockedSchedule>(new CommandDefinition(
            SelectStatusForUpdateSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        if (locked is null)
        {
            return new DeactivateScheduleResult(DeactivateScheduleOutcome.NotFound);
        }

        // Deactivating an inactive schedule changes nothing, so only an active one is updated.
        int cancelledTripCount = 0;
        if (locked.Status == ScheduleStatus.Active)
        {
            cancelledTripCount = await DeactivateAsync(writer, target, cancellationToken);
        }

        Schedule schedule = await GetScheduleAsync(writer, target.ScheduleId, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new DeactivateScheduleResult(DeactivateScheduleOutcome.Deactivated, ToResponse(schedule, cancelledTripCount));
    }

    /// <summary>Makes the schedule inactive and cancels its future trips; returns how many were cancelled.</summary>
    private async Task<int> DeactivateAsync(IWriterConnection writer, ScheduleTarget target, CancellationToken cancellationToken)
    {
        var deactivation = new { target.ScheduleId, Inactive = nameof(ScheduleStatus.Inactive) };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            DeactivateScheduleSql, deactivation, writer.Transaction, cancellationToken: cancellationToken));

        var cancellation = new
        {
            target.ScheduleId,
            target.CorrelationId,
            Reason = DeactivatedReason,
            Now = _clock.UtcNow,
        };
        return await writer.Connection.ExecuteAsync(new CommandDefinition(
            CancelFutureTripsSql, cancellation, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static async Task<Schedule> GetScheduleAsync(IWriterConnection writer, Guid scheduleId, CancellationToken cancellationToken)
    {
        var lookup = new { ScheduleId = scheduleId };
        ScheduleRow schedule = await writer.Connection.QuerySingleAsync<ScheduleRow>(new CommandDefinition(
            SelectScheduleSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        IEnumerable<FareRow> fares = await writer.Connection.QueryAsync<FareRow>(new CommandDefinition(
            SelectFaresSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        return ToSchedule(schedule, fares);
    }

    private static DeactivateScheduleResponse ToResponse(Schedule schedule, int cancelledTripCount)
    {
        return new DeactivateScheduleResponse
        {
            Schedule = schedule,
            CreatedTripCount = 0,
            CancelledTripCount = cancelledTripCount,
            RepricedTripCount = 0,
        };
    }

    private static Schedule ToSchedule(ScheduleRow schedule, IEnumerable<FareRow> fares)
    {
        var classFares = new List<ClassFare>();
        foreach (FareRow fare in fares)
        {
            classFares.Add(new ClassFare(fare.SeatClass, (int)(fare.Fare / TripRules.PaisePerRupee)));
        }

        var daysOfWeek = new List<DayOfWeek>();
        foreach (string day in schedule.DaysOfWeek)
        {
            daysOfWeek.Add(Enum.Parse<DayOfWeek>(day));
        }

        return new Schedule
        {
            ScheduleId = schedule.ScheduleId,
            OperatorId = schedule.OperatorId,
            RouteId = schedule.RouteId,
            BusId = schedule.BusId,
            DepartureTime = schedule.DepartureTime,
            DaysOfWeek = daysOfWeek,
            ClassFares = classFares,
            Status = schedule.Status,
            TripsGeneratedThrough = DateOnly.FromDateTime(schedule.TripsGeneratedThrough),
            CreatedAt = ToUtc(schedule.CreatedAt),
            UpdatedAt = ToUtc(schedule.UpdatedAt),
        };
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private sealed class LockedSchedule
    {
        public ScheduleStatus Status { get; init; }
    }

    private sealed class ScheduleRow
    {
        public Guid ScheduleId { get; init; }

        public string OperatorId { get; init; } = string.Empty;

        public Guid RouteId { get; init; }

        public Guid BusId { get; init; }

        public string DepartureTime { get; init; } = string.Empty;

        public string[] DaysOfWeek { get; init; } = Array.Empty<string>();

        public ScheduleStatus Status { get; init; }

        public DateTime TripsGeneratedThrough { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime UpdatedAt { get; init; }
    }

    // Fares are stored in paise and shown in whole rupees.
    private sealed class FareRow
    {
        public string SeatClass { get; init; } = string.Empty;

        public long Fare { get; init; }
    }
}
