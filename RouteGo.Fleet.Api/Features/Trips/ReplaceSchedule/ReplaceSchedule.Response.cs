namespace RouteGo.Fleet.Api.Features.Trips.ReplaceSchedule;

// Reading the schedule back as stored, so the response shows exactly what was kept and changed.
public sealed partial class ReplaceScheduleHandler
{
    private const string SelectScheduleSql = """
        SELECT schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status,
               trips_generated_through, created_at, updated_at
        FROM trips.schedules
        WHERE schedule_id = @ScheduleId
        """;

    // Fares follow the bus's seat-class order.
    private const string SelectFaresSql = """
        SELECT f.seat_class, f.fare
        FROM trips.schedule_class_fares f
        JOIN trips.schedules s ON s.schedule_id = f.schedule_id
        LEFT JOIN buses.seat_classes c ON c.bus_id = s.bus_id AND c.name = f.seat_class
        WHERE f.schedule_id = @ScheduleId
        ORDER BY c.display_order NULLS LAST, f.seat_class
        """;

    private static async Task<Schedule> GetScheduleAsync(IWriterConnection writer, Guid scheduleId, CancellationToken cancellationToken)
    {
        var lookup = new { ScheduleId = scheduleId };
        StoredScheduleRow schedule = await writer.Connection.QuerySingleAsync<StoredScheduleRow>(new CommandDefinition(
            SelectScheduleSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        IEnumerable<FareRow> fares = await writer.Connection.QueryAsync<FareRow>(new CommandDefinition(
            SelectFaresSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        return ToSchedule(schedule, fares);
    }

    private static Schedule ToSchedule(StoredScheduleRow schedule, IEnumerable<FareRow> fares)
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

    private sealed class StoredScheduleRow
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
