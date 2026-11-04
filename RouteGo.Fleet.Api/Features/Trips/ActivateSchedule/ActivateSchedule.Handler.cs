namespace RouteGo.Fleet.Api.Features.Trips.ActivateSchedule;

public sealed class ActivateScheduleHandler
{
    private const string AuditAction = "ActivateSchedule";

    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Only an inactive schedule is touched, so activating an active one writes no audit entry and
    // keeps its updated time. Ownership is in the WHERE: another operator's schedule is left alone.
    private const string ActivateScheduleSql = """
        UPDATE trips.schedules
        SET status = @Active
        WHERE schedule_id = @ScheduleId AND operator_id = @OperatorId AND status = @Inactive
        """;

    // Another operator's schedule is not found, exactly like one that does not exist, so the answer
    // reveals nothing about other operators' records.
    private const string SelectScheduleSql = """
        SELECT schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status,
               trips_generated_through, created_at, updated_at
        FROM trips.schedules
        WHERE schedule_id = @ScheduleId AND operator_id = @OperatorId
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

    public ActivateScheduleHandler(IWriterConnectionFactory writerFactory)
    {
        _writerFactory = writerFactory;
    }

    /// <summary>
    /// Makes one of the operator's schedules active again, provided the operator has its Fleet
    /// profile. Activation has no other precondition and generates no trips; an active schedule is
    /// returned unchanged.
    /// </summary>
    public async Task<ActivateScheduleResult> HandleAsync(Guid scheduleId, string operatorId, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new ActivateScheduleResult(ActivateScheduleOutcome.OperatorProfileNotReady);
        }

        var activation = new
        {
            ScheduleId = scheduleId,
            OperatorId = operatorId,
            Active = nameof(ScheduleStatus.Active),
            Inactive = nameof(ScheduleStatus.Inactive),
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            ActivateScheduleSql, activation, writer.Transaction, cancellationToken: cancellationToken));

        ActivateScheduleResponse? response = await GetScheduleAsync(writer, scheduleId, operatorId, cancellationToken);
        if (response is null)
        {
            return new ActivateScheduleResult(ActivateScheduleOutcome.NotFound);
        }

        await writer.CommitAsync(cancellationToken);
        return new ActivateScheduleResult(ActivateScheduleOutcome.Activated, response);
    }

    private static async Task<ActivateScheduleResponse?> GetScheduleAsync(
        IWriterConnection writer, Guid scheduleId, string operatorId, CancellationToken cancellationToken)
    {
        var lookup = new { ScheduleId = scheduleId, OperatorId = operatorId };
        ScheduleRow? schedule = await writer.Connection.QuerySingleOrDefaultAsync<ScheduleRow>(new CommandDefinition(
            SelectScheduleSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        if (schedule is null)
        {
            return null;
        }

        IEnumerable<FareRow> fares = await writer.Connection.QueryAsync<FareRow>(new CommandDefinition(
            SelectFaresSql, new { ScheduleId = scheduleId }, writer.Transaction, cancellationToken: cancellationToken));
        return ToResponse(schedule, fares);
    }

    private static ActivateScheduleResponse ToResponse(ScheduleRow schedule, IEnumerable<FareRow> fares)
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

        return new ActivateScheduleResponse
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
