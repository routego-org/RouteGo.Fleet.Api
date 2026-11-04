namespace RouteGo.Fleet.Api.Features.Trips.SearchTrips;

public sealed partial class SearchTripsHandler
{
    // The source and destination are the route's first and last stops; an intermediate stop never
    // matches either filter.
    private const string TripSourceSql = """
        FROM trips.trips t
        JOIN LATERAL (SELECT s.city_id, c.name FROM routes.route_stops s JOIN routes.cities c ON c.city_id = s.city_id
                      WHERE s.route_id = t.route_id ORDER BY s.sequence LIMIT 1) src ON true
        JOIN LATERAL (SELECT s.city_id, c.name FROM routes.route_stops s JOIN routes.cities c ON c.city_id = s.city_id
                      WHERE s.route_id = t.route_id ORDER BY s.sequence DESC LIMIT 1) dst ON true
        """;

    // Departed is never stored: a scheduled trip departing by now is departed, a later one is
    // scheduled, so the status filter becomes a stored status plus a departure bound.
    private const string TripFilterSql = """
        WHERE t.service_date BETWEEN @From AND @To
          AND (CAST(@OperatorId AS text) IS NULL OR t.operator_id = @OperatorId)
          AND (CAST(@SourceCityId AS uuid) IS NULL OR src.city_id = @SourceCityId)
          AND (CAST(@DestinationCityId AS uuid) IS NULL OR dst.city_id = @DestinationCityId)
          AND (CAST(@StoredStatus AS text) IS NULL OR t.status = @StoredStatus)
          AND (CAST(@DepartsAfter AS timestamptz) IS NULL OR t.departure_at > @DepartsAfter)
          AND (CAST(@DepartedBy AS timestamptz) IS NULL OR t.departure_at <= @DepartedBy)
        """;

    // The trip id breaks ties between trips departing at the same instant, so paging never repeats
    // or skips one.
    private const string SelectPageSql = $"""
        SELECT count(*)::int
        {TripSourceSql}
        {TripFilterSql};

        SELECT t.trip_id, t.schedule_id, t.operator_id, p.company_name AS operator_name, t.route_id,
               src.city_id AS source_city_id, src.name AS source_name,
               dst.city_id AS destination_city_id, dst.name AS destination_name,
               t.bus_id, b.name AS bus_name, t.service_date, t.departure_at, t.arrival_at, t.status,
               t.cancel_reason, t.cancelled_at
        {TripSourceSql}
        JOIN operator_profiles.operator_profiles p ON p.operator_id = t.operator_id
        JOIN buses.buses b ON b.bus_id = t.bus_id
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

    public SearchTripsHandler(IReaderConnectionFactory readerFactory, IClock clock)
    {
        _readerFactory = readerFactory;
        _clock = clock;
    }

    /// <summary>Lists one page of every operator's trips matching the filters in the range.</summary>
    public async Task<SearchTripsResponse> HandleAsync(SearchTripsRequest request, CancellationToken cancellationToken)
    {
        TripFilter filter = Normalize(request, _clock.UtcNow);
        return await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, filter, cancellationToken), cancellationToken);
    }

    private static async Task<SearchTripsResponse> ReadAsync(DbConnection connection, TripFilter filter, CancellationToken cancellationToken)
    {
        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(new CommandDefinition(
            SelectPageSql, ToParameters(filter), cancellationToken: cancellationToken));
        int totalCount = await results.ReadSingleAsync<int>();
        IEnumerable<TripRow> rows = await results.ReadAsync<TripRow>();
        var trips = rows.ToList();
        List<FareRow> fares = await ListFaresAsync(connection, trips, cancellationToken);
        return ToResponse(filter, totalCount, trips, fares);
    }

    private static async Task<List<FareRow>> ListFaresAsync(
        DbConnection connection, List<TripRow> trips, CancellationToken cancellationToken)
    {
        Guid[] tripIds = trips.Select(trip => trip.TripId).ToArray();
        IEnumerable<FareRow> fares = await connection.QueryAsync<FareRow>(new CommandDefinition(
            SelectFaresSql, new { TripIds = tripIds }, cancellationToken: cancellationToken));
        return fares.ToList();
    }

    private static TripFilter Normalize(SearchTripsRequest request, DateTimeOffset now)
    {
        DateOnly today = TripRules.GetServiceDate(now);
        DateOnly? from = SearchTripsValidator.ToDate(request.From);
        DateOnly? to = SearchTripsValidator.ToDate(request.To);
        return new TripFilter
        {
            Range = TripRules.GetListRange(today, from, to),
            OperatorId = ToOperatorId(request.OperatorId),
            SourceCityId = ToId(request.SourceCityId),
            DestinationCityId = ToId(request.DestinationCityId),
            Status = ToStatus(request.Status),
            Now = now,
            Page = Paging.Normalize(request.Page, request.PageSize),
        };
    }

    private static object ToParameters(TripFilter filter)
    {
        return new
        {
            filter.Range.From,
            filter.Range.To,
            filter.OperatorId,
            filter.SourceCityId,
            filter.DestinationCityId,
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

    // Operator ids are stored as text in their canonical lower-case UUID form; the validator has
    // already refused any value that is not a UUID.
    private static string? ToOperatorId(string? value)
    {
        Guid? operatorId = ToId(value);
        if (operatorId is null)
        {
            return null;
        }

        return operatorId.Value.ToString();
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

    /// <summary>Which trips, at what instant, and which page.</summary>
    private sealed class TripFilter
    {
        public required ServiceDateRange Range { get; init; }

        /// <summary>The operator to keep, as stored, or null for every operator.</summary>
        public required string? OperatorId { get; init; }

        public required Guid? SourceCityId { get; init; }

        public required Guid? DestinationCityId { get; init; }

        /// <summary>The status to keep, or null for every trip.</summary>
        public required TripStatus? Status { get; init; }

        /// <summary>The instant that decides which scheduled trips have departed.</summary>
        public required DateTimeOffset Now { get; init; }

        public required PageRequest Page { get; init; }
    }
}
