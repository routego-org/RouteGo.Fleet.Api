namespace RouteGo.Fleet.Api.Features.Routes.ListRoutes;

/// <summary>Which of the operator's routes to list, and the page wanted.</summary>
public sealed class ListRoutesRequest
{
    /// <summary>active or inactive; every route when absent.</summary>
    public string? Status { get; init; }

    /// <summary>1-based page number; 1 when absent.</summary>
    public int? Page { get; init; }

    /// <summary>Items per page, 1–200; 20 when absent.</summary>
    public int? PageSize { get; init; }
}

/// <summary>A place a route can start at, stop at or end at.</summary>
/// <param name="CityId">The city's identifier.</param>
/// <param name="Name">The city's name, unique within its state.</param>
/// <param name="State">The Indian state or union territory the city is in.</param>
public sealed record City(Guid CityId, string Name, string State);

/// <summary>One stop on a route.</summary>
/// <param name="Sequence">The stop's place in the route, 1 for the source.</param>
/// <param name="City">The city.</param>
/// <param name="OffsetMinutes">Minutes after departure at which the stop is reached.</param>
public sealed record RouteStop(int Sequence, City City, int OffsetMinutes);

/// <summary>One route with every stop in order.</summary>
public sealed class RouteResponse
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

/// <summary>One page of the operator's routes, by source city, destination city, then creation time.</summary>
public sealed class ListRoutesResponse
{
    public required IReadOnlyList<RouteResponse> Items { get; init; }

    /// <summary>How many routes match across all pages.</summary>
    public required int TotalCount { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }
}

public enum ListRoutesOutcome
{
    Listed,
    OperatorProfileNotReady,
}

/// <summary>How the listing ended, with the page when it was listed.</summary>
/// <param name="Outcome">How the listing ended.</param>
/// <param name="Response">The page when listed.</param>
public sealed record ListRoutesResult(ListRoutesOutcome Outcome, ListRoutesResponse? Response = null);

[ApiController]
[ApiExplorerSettings(GroupName = "Routes")]
[Route("routes")]
public sealed class ListRoutesController : ControllerBase
{
    private readonly ListRoutesHandler _handler;
    private readonly ListRoutesValidator _validator = new ListRoutesValidator();

    public ListRoutesController(IReaderConnectionFactory readerFactory)
    {
        _handler = new ListRoutesHandler(readerFactory);
    }

    /// <summary>List the operator's own routes (Operator only).</summary>
    /// <remarks>
    /// Lists the calling operator's routes, sorted by source city name, destination city name and
    /// creation time, optionally filtered by status. Requires role Operator and the operator's
    /// Fleet profile. No routes is an empty page, not an error.
    /// </remarks>
    /// <param name="request">The status filter and the page wanted.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <response code="200">The requested page.</response>
    /// <response code="400">The status, page or page size is invalid (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpGet]
    [ProducesResponseType(typeof(ListRoutesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListRoutesAsync([FromQuery] ListRoutesRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        ListRoutesResult result = await _handler.HandleAsync(request, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            case ListRoutesOutcome.Listed:
                return Ok(result.Response);
            case ListRoutesOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
