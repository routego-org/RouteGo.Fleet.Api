namespace RouteGo.Fleet.Api.Features.Routes.GetRoute;

/// <summary>A place a route can start at, stop at or end at.</summary>
/// <param name="CityId">The city's identifier.</param>
/// <param name="Name">The city's name, unique within its state.</param>
/// <param name="State">The Indian state or union territory the city is in.</param>
public sealed record City(Guid CityId, string Name, string State);

/// <summary>One stop on the route.</summary>
/// <param name="Sequence">The stop's place in the route, 1 for the source.</param>
/// <param name="City">The city.</param>
/// <param name="OffsetMinutes">Minutes after departure at which the stop is reached.</param>
public sealed record RouteStop(int Sequence, City City, int OffsetMinutes);

/// <summary>The route with every stop in order.</summary>
public sealed class GetRouteResponse
{
    public required Guid RouteId { get; init; }

    public required string OperatorId { get; init; }

    /// <summary>The first stop's city.</summary>
    public required City Source { get; init; }

    /// <summary>The last stop's city.</summary>
    public required City Destination { get; init; }

    public required IReadOnlyList<RouteStop> Stops { get; init; }

    public required int DistanceKm { get; init; }

    /// <summary>The destination's offset.</summary>
    public required int JourneyMinutes { get; init; }

    public required RouteStatus Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null until the route is deactivated.</summary>
    public required DateTimeOffset? DeactivatedAt { get; init; }
}

public enum GetRouteOutcome
{
    Found,
    OperatorProfileNotReady,
    NotFound,
}

/// <summary>How the read ended, with the route when it was found.</summary>
/// <param name="Outcome">How the read ended.</param>
/// <param name="Response">The route when found.</param>
public sealed record GetRouteResult(GetRouteOutcome Outcome, GetRouteResponse? Response = null);

public sealed class GetRouteHandler
{
    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Ownership is part of the lookup: another operator's route is not found, exactly like one that
    // does not exist, so the answer reveals nothing about other operators' records.
    private const string SelectRouteSql = """
        SELECT route_id, operator_id, distance_km::int AS distance_km, status, created_at, deactivated_at
        FROM routes.routes
        WHERE route_id = @RouteId AND operator_id = @OperatorId
        """;

    private const string SelectStopsSql = """
        SELECT s.sequence::int AS sequence, s.offset_minutes::int AS offset_minutes, c.city_id, c.name, c.state
        FROM routes.route_stops s
        JOIN routes.cities c ON c.city_id = s.city_id
        WHERE s.route_id = @RouteId
        ORDER BY s.sequence
        """;

    private readonly IReaderConnectionFactory _readerFactory;

    public GetRouteHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    /// <summary>Reads one of the operator's routes, provided the operator has its Fleet profile.</summary>
    public async Task<GetRouteResult> HandleAsync(Guid routeId, string operatorId, CancellationToken cancellationToken)
    {
        return await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, routeId, operatorId, cancellationToken), cancellationToken);
    }

    private static async Task<GetRouteResult> ReadAsync(
        DbConnection connection, Guid routeId, string operatorId, CancellationToken cancellationToken)
    {
        bool hasProfile = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { OperatorId = operatorId }, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new GetRouteResult(GetRouteOutcome.OperatorProfileNotReady);
        }

        var lookup = new { RouteId = routeId, OperatorId = operatorId };
        RouteRow? route = await connection.QuerySingleOrDefaultAsync<RouteRow>(new CommandDefinition(
            SelectRouteSql, lookup, cancellationToken: cancellationToken));
        if (route is null)
        {
            return new GetRouteResult(GetRouteOutcome.NotFound);
        }

        IEnumerable<StopRow> stops = await connection.QueryAsync<StopRow>(new CommandDefinition(
            SelectStopsSql, new { RouteId = routeId }, cancellationToken: cancellationToken));
        return new GetRouteResult(GetRouteOutcome.Found, ToResponse(route, stops));
    }

    private static GetRouteResponse ToResponse(RouteRow route, IEnumerable<StopRow> rows)
    {
        var stops = new List<RouteStop>();
        foreach (StopRow row in rows)
        {
            stops.Add(new RouteStop(row.Sequence, new City(row.CityId, row.Name, row.State), row.OffsetMinutes));
        }

        DateTimeOffset? deactivatedAt = null;
        if (route.DeactivatedAt != null)
        {
            deactivatedAt = ToUtc(route.DeactivatedAt.Value);
        }

        return new GetRouteResponse
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
        public int Sequence { get; init; }

        public int OffsetMinutes { get; init; }

        public Guid CityId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string State { get; init; } = string.Empty;
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Routes")]
[Route("routes")]
public sealed class GetRouteController : ControllerBase
{
    private readonly GetRouteHandler _handler;

    public GetRouteController(IReaderConnectionFactory readerFactory)
    {
        _handler = new GetRouteHandler(readerFactory);
    }

    /// <summary>Read one of the operator's routes (Operator only).</summary>
    /// <remarks>
    /// Returns the route with every stop in order. Requires role Operator and the operator's Fleet
    /// profile. A route that belongs to another operator is reported as not found.
    /// </remarks>
    /// <param name="routeId">The route's identifier.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <response code="200">The route.</response>
    /// <response code="400">The route identifier is not a UUID (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="404">No route with this identifier belongs to the caller (NOT_FOUND).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpGet("{routeId}")]
    [ProducesResponseType(typeof(GetRouteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRouteAsync(Guid routeId, CancellationToken cancellationToken)
    {
        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        GetRouteResult result = await _handler.HandleAsync(routeId, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            case GetRouteOutcome.Found:
                return Ok(result.Response);
            case GetRouteOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case GetRouteOutcome.NotFound:
                return ProblemResult.NotFound(this, $"You have no route {routeId}.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
