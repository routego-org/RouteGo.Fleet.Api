namespace RouteGo.Fleet.Api.Features.Trips.ListTrips;

public sealed partial class ListTripsHandler
{
    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Departed is never stored: a scheduled trip departing by now is departed, a later one is
    // scheduled, so the status filter becomes a stored status plus a departure bound.
    private const string TripFilterSql = """
        WHERE t.operator_id = @OperatorId
          AND t.service_date BETWEEN @From AND @To
          AND (CAST(@ScheduleId AS uuid) IS NULL OR t.schedule_id = @ScheduleId)
          AND (CAST(@BusId AS uuid) IS NULL OR t.bus_id = @BusId)
          AND (CAST(@StoredStatus AS text) IS NULL OR t.status = @StoredStatus)
          AND (CAST(@DepartsAfter AS timestamptz) IS NULL OR t.departure_at > @DepartsAfter)
          AND (CAST(@DepartedBy AS timestamptz) IS NULL OR t.departure_at <= @DepartedBy)
        """;

    // The trip id breaks ties between trips departing at the same instant, so paging never repeats
    // or skips one. The source and destination are the route's first and last stops.
    private const string SelectPageSql = $"""
        SELECT count(*)::int
        FROM trips.trips t
        {TripFilterSql};

        SELECT t.trip_id, t.schedule_id, t.operator_id, p.company_name AS operator_name, t.route_id,
               src.city_id AS source_city_id, src.name AS source_name,
               dst.city_id AS destination_city_id, dst.name AS destination_name,
               t.bus_id, b.name AS bus_name, t.service_date, t.departure_at, t.arrival_at, t.status,
               t.cancel_reason, t.cancelled_at
        FROM trips.trips t
        JOIN operator_profiles.operator_profiles p ON p.operator_id = t.operator_id
        JOIN buses.buses b ON b.bus_id = t.bus_id
        JOIN LATERAL (SELECT s.city_id, c.name FROM routes.route_stops s JOIN routes.cities c ON c.city_id = s.city_id
                      WHERE s.route_id = t.route_id ORDER BY s.sequence LIMIT 1) src ON true
        JOIN LATERAL (SELECT s.city_id, c.name FROM routes.route_stops s JOIN routes.cities c ON c.city_id = s.city_id
                      WHERE s.route_id = t.route_id ORDER BY s.sequence DESC LIMIT 1) dst ON true
        {TripFilterSql}
        ORDER BY t.departure_at, t.trip_id
        OFFSET @Offset LIMIT @Limit;
        """;

    // Every fare on the page in one read, in the bus's seat-class order; a class the bus has since
    // dropped goes last, by name.
    private const string SelectFaresSql = """
        SELECT f.trip_id, f.seat_class, f.fare
        FROM trips.trip_class_fares f
        JOIN trips.trips t ON t.trip_id = f.trip_id
        LEFT JOIN buses.seat_classes c ON c.bus_id = t.bus_id AND c.name = f.seat_class
        WHERE f.trip_id = ANY(@TripIds)
        ORDER BY f.trip_id, c.display_order NULLS LAST, f.seat_class
        """;

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly IClock _clock;

    public ListTripsHandler(IReaderConnectionFactory readerFactory, IClock clock)
    {
        _readerFactory = readerFactory;
        _clock = clock;
    }

    /// <summary>Lists one page of the operator's trips in the range, provided the operator has its Fleet profile.</summary>
    public async Task<ListTripsResult> HandleAsync(ListTripsRequest request, string operatorId, CancellationToken cancellationToken)
    {
        TripFilter filter = Normalize(request, operatorId, _clock.UtcNow);
        return await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, filter, cancellationToken), cancellationToken);
    }

    private static async Task<ListTripsResult> ReadAsync(DbConnection connection, TripFilter filter, CancellationToken cancellationToken)
    {
        bool hasProfile = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { filter.OperatorId }, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new ListTripsResult(ListTripsOutcome.OperatorProfileNotReady);
        }

        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(new CommandDefinition(
            SelectPageSql, ToParameters(filter), cancellationToken: cancellationToken));
        int totalCount = await results.ReadSingleAsync<int>();
        IEnumerable<TripRow> rows = await results.ReadAsync<TripRow>();
        var trips = rows.ToList();
        List<FareRow> fares = await ListFaresAsync(connection, trips, cancellationToken);
        ListTripsResponse response = ToResponse(filter, totalCount, trips, fares);
        return new ListTripsResult(ListTripsOutcome.Listed, response);
    }

    private static async Task<List<FareRow>> ListFaresAsync(
        DbConnection connection, List<TripRow> trips, CancellationToken cancellationToken)
    {
        Guid[] tripIds = trips.Select(trip => trip.TripId).ToArray();
        IEnumerable<FareRow> fares = await connection.QueryAsync<FareRow>(new CommandDefinition(
            SelectFaresSql, new { TripIds = tripIds }, cancellationToken: cancellationToken));
        return fares.ToList();
    }

    private static TripFilter Normalize(ListTripsRequest request, string operatorId, DateTimeOffset now)
    {
        DateOnly today = TripRules.GetServiceDate(now);
        DateOnly? from = ListTripsValidator.ToDate(request.From);
        DateOnly? to = ListTripsValidator.ToDate(request.To);
        TripStatus? status = ToStatus(request.Status);
        return new TripFilter
        {
            OperatorId = operatorId,
            Range = TripRules.GetListRange(today, from, to),
            ScheduleId = ToId(request.ScheduleId),
            BusId = ToId(request.BusId),
            Status = status,
            Now = now,
            Page = Paging.Normalize(request.Page, request.PageSize),
        };
    }

    private static object ToParameters(TripFilter filter)
    {
        return new
        {
            filter.OperatorId,
            filter.Range.From,
            filter.Range.To,
            filter.ScheduleId,
            filter.BusId,
            StoredStatus = ToStoredStatus(filter.Status),
            DepartsAfter = GetDepartureBound(filter, TripStatus.Scheduled),
            DepartedBy = GetDepartureBound(filter, TripStatus.Departed),
            filter.Page.Offset,
            Limit = filter.Page.PageSize,
        };
    }

    // Scheduled and departed trips are both stored as Scheduled.
    private static string? ToStoredStatus(TripStatus? status)
    {
        if (status == TripStatus.Cancelled)
        {
            return nameof(TripStatus.Cancelled);
        }

        if (status is null)
        {
            return null;
        }

        return nameof(TripStatus.Scheduled);
    }

    // Now, when the filter asks for exactly this status; otherwise no bound.
    private static DateTimeOffset? GetDepartureBound(TripFilter filter, TripStatus status)
    {
        if (filter.Status != status)
        {
            return null;
        }

        return filter.Now;
    }

    // The validator has already refused any spelling that is not a trip status.
    private static TripStatus? ToStatus(string? status)
    {
        if (!WireJson.TryReadEnum(status, out TripStatus value))
        {
            return null;
        }

        return value;
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

    /// <summary>Whose trips, which ones, at what instant, and which page.</summary>
    private sealed class TripFilter
    {
        public required string OperatorId { get; init; }

        public required ServiceDateRange Range { get; init; }

        public required Guid? ScheduleId { get; init; }

        public required Guid? BusId { get; init; }

        /// <summary>The status to keep, or null for every trip.</summary>
        public required TripStatus? Status { get; init; }

        /// <summary>The instant that decides which scheduled trips have departed.</summary>
        public required DateTimeOffset Now { get; init; }

        public required PageRequest Page { get; init; }
    }
}
