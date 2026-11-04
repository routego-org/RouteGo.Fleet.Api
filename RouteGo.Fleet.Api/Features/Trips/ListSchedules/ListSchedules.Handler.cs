namespace RouteGo.Fleet.Api.Features.Trips.ListSchedules;

public sealed class ListSchedulesHandler
{
    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // departure_time is HH:mm, so its text order is its time order. The schedule id breaks ties
    // between schedules created at the same instant, so paging never repeats or skips one.
    private const string SelectPageSql = """
        SELECT count(*)::int
        FROM trips.schedules
        WHERE operator_id = @OperatorId
          AND (CAST(@Status AS text) IS NULL OR status = @Status)
          AND (CAST(@RouteId AS uuid) IS NULL OR route_id = @RouteId)
          AND (CAST(@BusId AS uuid) IS NULL OR bus_id = @BusId);

        SELECT schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status,
               trips_generated_through, created_at, updated_at
        FROM trips.schedules
        WHERE operator_id = @OperatorId
          AND (CAST(@Status AS text) IS NULL OR status = @Status)
          AND (CAST(@RouteId AS uuid) IS NULL OR route_id = @RouteId)
          AND (CAST(@BusId AS uuid) IS NULL OR bus_id = @BusId)
        ORDER BY departure_time, created_at, schedule_id
        OFFSET @Offset LIMIT @Limit;
        """;

    // Every fare on the page in one read. Fares follow the bus's seat-class order; a class the bus
    // has since dropped goes last, by name.
    private const string SelectFaresSql = """
        SELECT f.schedule_id, f.seat_class, f.fare
        FROM trips.schedule_class_fares f
        JOIN trips.schedules s ON s.schedule_id = f.schedule_id
        LEFT JOIN buses.seat_classes c ON c.bus_id = s.bus_id AND c.name = f.seat_class
        WHERE f.schedule_id = ANY(@ScheduleIds)
        ORDER BY f.schedule_id, c.display_order NULLS LAST, f.seat_class
        """;

    private readonly IReaderConnectionFactory _readerFactory;

    public ListSchedulesHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    /// <summary>Lists one page of the operator's schedules, provided the operator has its Fleet profile.</summary>
    public async Task<ListSchedulesResult> HandleAsync(
        ListSchedulesRequest request, string operatorId, CancellationToken cancellationToken)
    {
        var filter = new ScheduleFilter
        {
            OperatorId = operatorId,
            Status = ToStatusColumn(request.Status),
            RouteId = ToId(request.RouteId),
            BusId = ToId(request.BusId),
            Page = Paging.Normalize(request.Page, request.PageSize),
        };
        return await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, filter, cancellationToken), cancellationToken);
    }

    private static async Task<ListSchedulesResult> ReadAsync(
        DbConnection connection, ScheduleFilter filter, CancellationToken cancellationToken)
    {
        bool hasProfile = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { filter.OperatorId }, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new ListSchedulesResult(ListSchedulesOutcome.OperatorProfileNotReady);
        }

        var parameters = new
        {
            filter.OperatorId,
            filter.Status,
            filter.RouteId,
            filter.BusId,
            filter.Page.Offset,
            Limit = filter.Page.PageSize,
        };
        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(new CommandDefinition(
            SelectPageSql, parameters, cancellationToken: cancellationToken));
        int totalCount = await results.ReadSingleAsync<int>();
        IEnumerable<ScheduleRow> rows = await results.ReadAsync<ScheduleRow>();
        var schedules = rows.ToList();
        List<FareRow> fares = await ListFaresAsync(connection, schedules, cancellationToken);
        ListSchedulesResponse response = ToResponse(filter.Page, totalCount, schedules, fares);
        return new ListSchedulesResult(ListSchedulesOutcome.Listed, response);
    }

    private static async Task<List<FareRow>> ListFaresAsync(
        DbConnection connection, List<ScheduleRow> schedules, CancellationToken cancellationToken)
    {
        Guid[] scheduleIds = schedules.Select(schedule => schedule.ScheduleId).ToArray();
        IEnumerable<FareRow> fares = await connection.QueryAsync<FareRow>(new CommandDefinition(
            SelectFaresSql, new { ScheduleIds = scheduleIds }, cancellationToken: cancellationToken));
        return fares.ToList();
    }

    // The validator has already refused any spelling that is not a schedule status.
    private static string? ToStatusColumn(string? status)
    {
        if (!WireJson.TryReadEnum(status, out ScheduleStatus value))
        {
            return null;
        }

        return value.ToString();
    }

    // The validator has already refused any value that is not a UUID.
    private static Guid? ToId(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return Guid.Parse(value);
    }

    private static ListSchedulesResponse ToResponse(
        PageRequest page, int totalCount, List<ScheduleRow> schedules, List<FareRow> fares)
    {
        var items = new List<Schedule>();
        foreach (ScheduleRow schedule in schedules)
        {
            var scheduleFares = fares.Where(fare => fare.ScheduleId == schedule.ScheduleId).ToList();
            items.Add(ToSchedule(schedule, scheduleFares));
        }

        return new ListSchedulesResponse { Items = items, TotalCount = totalCount, Page = page.Page, PageSize = page.PageSize };
    }

    private static Schedule ToSchedule(ScheduleRow schedule, List<FareRow> fares)
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

    /// <summary>Whose schedules, which ones, and which page.</summary>
    private sealed class ScheduleFilter
    {
        public required string OperatorId { get; init; }

        /// <summary>The stored status to keep, or null for every schedule.</summary>
        public required string? Status { get; init; }

        public required Guid? RouteId { get; init; }

        public required Guid? BusId { get; init; }

        public required PageRequest Page { get; init; }
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
        public Guid ScheduleId { get; init; }

        public string SeatClass { get; init; } = string.Empty;

        public long Fare { get; init; }
    }
}
