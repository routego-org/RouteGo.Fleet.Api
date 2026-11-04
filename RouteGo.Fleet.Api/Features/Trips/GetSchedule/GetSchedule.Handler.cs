namespace RouteGo.Fleet.Api.Features.Trips.GetSchedule;

public sealed class GetScheduleHandler
{
    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Ownership is part of the lookup: another operator's schedule is not found, exactly like one
    // that does not exist, so the answer reveals nothing about other operators' records.
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

    private readonly IReaderConnectionFactory _readerFactory;

    public GetScheduleHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    /// <summary>Reads one of the operator's schedules with its class fares, provided the operator has its Fleet profile.</summary>
    public async Task<GetScheduleResult> HandleAsync(Guid scheduleId, string operatorId, CancellationToken cancellationToken)
    {
        return await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, scheduleId, operatorId, cancellationToken), cancellationToken);
    }

    private static async Task<GetScheduleResult> ReadAsync(
        DbConnection connection, Guid scheduleId, string operatorId, CancellationToken cancellationToken)
    {
        bool hasProfile = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { OperatorId = operatorId }, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new GetScheduleResult(GetScheduleOutcome.OperatorProfileNotReady);
        }

        var lookup = new { ScheduleId = scheduleId, OperatorId = operatorId };
        ScheduleRow? schedule = await connection.QuerySingleOrDefaultAsync<ScheduleRow>(new CommandDefinition(
            SelectScheduleSql, lookup, cancellationToken: cancellationToken));
        if (schedule is null)
        {
            return new GetScheduleResult(GetScheduleOutcome.NotFound);
        }

        IEnumerable<FareRow> fares = await connection.QueryAsync<FareRow>(new CommandDefinition(
            SelectFaresSql, new { ScheduleId = scheduleId }, cancellationToken: cancellationToken));
        return new GetScheduleResult(GetScheduleOutcome.Found, ToResponse(schedule, fares));
    }

    private static GetScheduleResponse ToResponse(ScheduleRow schedule, IEnumerable<FareRow> fares)
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

        return new GetScheduleResponse
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
