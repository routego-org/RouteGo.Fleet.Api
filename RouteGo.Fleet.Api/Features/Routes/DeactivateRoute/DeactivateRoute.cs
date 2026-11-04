namespace RouteGo.Fleet.Api.Features.Routes.DeactivateRoute;

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

/// <summary>The route, now inactive, with every stop in order.</summary>
public sealed class DeactivateRouteResponse
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

    /// <summary>When the route was first deactivated.</summary>
    public required DateTimeOffset? DeactivatedAt { get; init; }
}

public enum DeactivateRouteOutcome
{
    Deactivated,
    OperatorProfileNotReady,
    NotFound,
}

/// <summary>How the deactivation ended, with the route when it was found.</summary>
/// <param name="Outcome">How the deactivation ended.</param>
/// <param name="Response">The inactive route when found.</param>
public sealed record DeactivateRouteResult(DeactivateRouteOutcome Outcome, DeactivateRouteResponse? Response = null);

public sealed class DeactivateRouteHandler
{
    private const string AuditAction = "DeactivateRoute";

    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Only an active route is touched, so deactivating twice keeps the first deactivation time and
    // writes no second audit entry. Ownership is in the WHERE: another operator's route is left alone.
    private const string DeactivateRouteSql = """
        UPDATE routes.routes
        SET status = @Inactive, deactivated_at = @DeactivatedAt
        WHERE route_id = @RouteId AND operator_id = @OperatorId AND status = @Active
        """;

    // Another operator's route is not found, exactly like one that does not exist, so the answer
    // reveals nothing about other operators' records.
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

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;

    public DeactivateRouteHandler(IWriterConnectionFactory writerFactory, IClock clock)
    {
        _writerFactory = writerFactory;
        _clock = clock;
    }

    /// <summary>
    /// Makes one of the operator's routes inactive, provided the operator has its Fleet profile.
    /// Schedules and trips already on the route are not touched.
    /// </summary>
    public async Task<DeactivateRouteResult> HandleAsync(Guid routeId, string operatorId, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new DeactivateRouteResult(DeactivateRouteOutcome.OperatorProfileNotReady);
        }

        await DeactivateIfActiveAsync(writer, routeId, operatorId, cancellationToken);
        RouteRow? route = await writer.Connection.QuerySingleOrDefaultAsync<RouteRow>(new CommandDefinition(
            SelectRouteSql, new { RouteId = routeId, OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (route is null)
        {
            return new DeactivateRouteResult(DeactivateRouteOutcome.NotFound);
        }

        IEnumerable<StopRow> stops = await writer.Connection.QueryAsync<StopRow>(new CommandDefinition(
            SelectStopsSql, new { RouteId = routeId }, writer.Transaction, cancellationToken: cancellationToken));
        await writer.CommitAsync(cancellationToken);
        return new DeactivateRouteResult(DeactivateRouteOutcome.Deactivated, ToResponse(route, stops));
    }

    private async Task DeactivateIfActiveAsync(
        IWriterConnection writer, Guid routeId, string operatorId, CancellationToken cancellationToken)
    {
        var deactivation = new
        {
            RouteId = routeId,
            OperatorId = operatorId,
            DeactivatedAt = _clock.UtcNow,
            Active = nameof(RouteStatus.Active),
            Inactive = nameof(RouteStatus.Inactive),
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            DeactivateRouteSql, deactivation, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static DeactivateRouteResponse ToResponse(RouteRow route, IEnumerable<StopRow> rows)
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

        return new DeactivateRouteResponse
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
public sealed class DeactivateRouteController : ControllerBase
{
    private readonly DeactivateRouteHandler _handler;

    public DeactivateRouteController(IWriterConnectionFactory writerFactory, IClock clock)
    {
        _handler = new DeactivateRouteHandler(writerFactory, clock);
    }

    /// <summary>Stop a route being used by new schedules (Operator only).</summary>
    /// <remarks>
    /// Makes the route inactive: no new schedule, and no schedule edit moving to it, may use it.
    /// Existing schedules and trips on it are unaffected and no event is recorded. Deactivating an
    /// inactive route changes nothing. Requires role Operator and the operator's Fleet profile. A
    /// route that belongs to another operator is reported as not found.
    /// </remarks>
    /// <param name="routeId">The route's identifier.</param>
    /// <param name="cancellationToken">Cancels the deactivation.</param>
    /// <response code="200">The route is inactive.</response>
    /// <response code="400">The route identifier is not a UUID (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="404">No route with this identifier belongs to the caller (NOT_FOUND).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpPost("{routeId}/deactivate")]
    [ProducesResponseType(typeof(DeactivateRouteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateRouteAsync(Guid routeId, CancellationToken cancellationToken)
    {
        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        DeactivateRouteResult result = await _handler.HandleAsync(routeId, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            case DeactivateRouteOutcome.Deactivated:
                return Ok(result.Response);
            case DeactivateRouteOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case DeactivateRouteOutcome.NotFound:
                return ProblemResult.NotFound(this, $"You have no route {routeId}.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
