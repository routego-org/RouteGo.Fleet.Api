namespace RouteGo.Fleet.Api.Features.Routes.ListRoutes;

public sealed class ListRoutesHandler
{
    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Source and destination are not stored on the route: they are its first and last stops, so the
    // sort joins both stops to their cities. The route id breaks ties so paging never repeats or
    // skips a route created in the same instant as another.
    private const string SelectPageSql = """
        SELECT count(*)::int
        FROM routes.routes
        WHERE operator_id = @OperatorId
          AND (CAST(@Status AS text) IS NULL OR status = @Status);

        SELECT r.route_id, r.operator_id, r.distance_km::int AS distance_km, r.status, r.created_at, r.deactivated_at
        FROM routes.routes r
        JOIN routes.route_stops first_stop ON first_stop.route_id = r.route_id AND first_stop.sequence = 1
        JOIN routes.cities source_city ON source_city.city_id = first_stop.city_id
        JOIN routes.route_stops last_stop ON last_stop.route_id = r.route_id
          AND last_stop.sequence = (SELECT max(s.sequence) FROM routes.route_stops s WHERE s.route_id = r.route_id)
        JOIN routes.cities destination_city ON destination_city.city_id = last_stop.city_id
        WHERE r.operator_id = @OperatorId
          AND (CAST(@Status AS text) IS NULL OR r.status = @Status)
        ORDER BY source_city.name, destination_city.name, r.created_at, r.route_id
        OFFSET @Offset LIMIT @Limit;
        """;

    private const string SelectStopsSql = """
        SELECT s.route_id, s.sequence::int AS sequence, s.offset_minutes::int AS offset_minutes, c.city_id, c.name, c.state
        FROM routes.route_stops s
        JOIN routes.cities c ON c.city_id = s.city_id
        WHERE s.route_id = ANY(@RouteIds)
        ORDER BY s.route_id, s.sequence
        """;

    private readonly IReaderConnectionFactory _readerFactory;

    public ListRoutesHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    /// <summary>Lists one page of the operator's routes, provided the operator has its Fleet profile.</summary>
    public async Task<ListRoutesResult> HandleAsync(
        ListRoutesRequest request, string operatorId, CancellationToken cancellationToken)
    {
        var filter = new RouteFilter(operatorId, ToStatusColumn(request.Status), Paging.Normalize(request.Page, request.PageSize));
        return await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, filter, cancellationToken), cancellationToken);
    }

    private static async Task<ListRoutesResult> ReadAsync(
        DbConnection connection, RouteFilter filter, CancellationToken cancellationToken)
    {
        bool hasProfile = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { filter.OperatorId }, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new ListRoutesResult(ListRoutesOutcome.OperatorProfileNotReady);
        }

        ListRoutesResponse response = await ListPageAsync(connection, filter, cancellationToken);
        return new ListRoutesResult(ListRoutesOutcome.Listed, response);
    }

    private static async Task<ListRoutesResponse> ListPageAsync(
        DbConnection connection, RouteFilter filter, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            filter.OperatorId,
            filter.Status,
            filter.Page.Offset,
            Limit = filter.Page.PageSize,
        };
        int totalCount;
        List<RouteRow> routes;
        await using (SqlMapper.GridReader results = await connection.QueryMultipleAsync(new CommandDefinition(
            SelectPageSql, parameters, cancellationToken: cancellationToken)))
        {
            totalCount = await results.ReadSingleAsync<int>();
            IEnumerable<RouteRow> rows = await results.ReadAsync<RouteRow>();
            routes = rows.ToList();
        }

        Guid[] routeIds = routes.Select(route => route.RouteId).ToArray();
        IEnumerable<StopRow> stops = await connection.QueryAsync<StopRow>(new CommandDefinition(
            SelectStopsSql, new { RouteIds = routeIds }, cancellationToken: cancellationToken));
        return ToResponse(filter.Page, totalCount, routes, stops);
    }

    // The validator has already refused any spelling that is not a route status.
    private static string? ToStatusColumn(string? status)
    {
        if (!WireJson.TryReadEnum(status, out RouteStatus value))
        {
            return null;
        }

        return value.ToString();
    }

    private static ListRoutesResponse ToResponse(
        PageRequest page, int totalCount, List<RouteRow> routes, IEnumerable<StopRow> stopRows)
    {
        Dictionary<Guid, List<RouteStop>> stopsByRoute = GroupByRoute(stopRows);
        var items = new List<RouteResponse>();
        foreach (RouteRow route in routes)
        {
            items.Add(ToRoute(route, stopsByRoute[route.RouteId]));
        }

        return new ListRoutesResponse { Items = items, TotalCount = totalCount, Page = page.Page, PageSize = page.PageSize };
    }

    private static Dictionary<Guid, List<RouteStop>> GroupByRoute(IEnumerable<StopRow> rows)
    {
        var stopsByRoute = new Dictionary<Guid, List<RouteStop>>();
        foreach (StopRow row in rows)
        {
            if (!stopsByRoute.TryGetValue(row.RouteId, out List<RouteStop>? stops))
            {
                stops = new List<RouteStop>();
                stopsByRoute.Add(row.RouteId, stops);
            }

            stops.Add(new RouteStop(row.Sequence, new City(row.CityId, row.Name, row.State), row.OffsetMinutes));
        }

        return stopsByRoute;
    }

    private static RouteResponse ToRoute(RouteRow route, List<RouteStop> stops)
    {
        DateTimeOffset? deactivatedAt = null;
        if (route.DeactivatedAt != null)
        {
            deactivatedAt = ToUtc(route.DeactivatedAt.Value);
        }

        return new RouteResponse
        {
            RouteId = route.RouteId,
            OperatorId = route.OperatorId,
            Source = stops[0].City,
            Destination = stops[^1].City,
            Stops = stops,
            DistanceKm = route.DistanceKm,
            JourneyMinutes = stops[^1].OffsetMinutes,
            Status = route.Status,
            CreatedAt = ToUtc(route.CreatedAt),
            DeactivatedAt = deactivatedAt,
        };
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    /// <param name="OperatorId">The calling operator; only its routes are listed.</param>
    /// <param name="Status">The stored status to keep, or null for every route.</param>
    /// <param name="Page">The page wanted.</param>
    private sealed record RouteFilter(string OperatorId, string? Status, PageRequest Page);

    private sealed class RouteRow
    {
        public Guid RouteId { get; init; }

        public string OperatorId { get; init; } = string.Empty;

        public int DistanceKm { get; init; }

        public RouteStatus Status { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime? DeactivatedAt { get; init; }
    }

    private sealed class StopRow
    {
        public Guid RouteId { get; init; }

        public int Sequence { get; init; }

        public int OffsetMinutes { get; init; }

        public Guid CityId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string State { get; init; } = string.Empty;
    }
}
