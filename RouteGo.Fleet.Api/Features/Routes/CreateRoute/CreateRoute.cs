namespace RouteGo.Fleet.Api.Features.Routes.CreateRoute;

/// <summary>The route to create: its stops in order and its distance. A route never changes once created.</summary>
public sealed class CreateRouteRequest
{
    /// <summary>2–12 stops in order: the source first (offset 0), the destination last.</summary>
    public IReadOnlyList<RouteStopInput>? Stops { get; init; }

    /// <summary>Whole kilometres, 1–3,000.</summary>
    public int? DistanceKm { get; init; }
}

/// <summary>One stop: a city and when the bus reaches it.</summary>
public sealed class RouteStopInput
{
    /// <summary>A city from the city list; no city may appear twice.</summary>
    public Guid? CityId { get; init; }

    /// <summary>Minutes after departure: 0 for the first stop, strictly increasing, at most 2,880.</summary>
    public int? OffsetMinutes { get; init; }
}

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

/// <summary>The route as created.</summary>
public sealed class CreateRouteResponse
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

public enum CreateRouteOutcome
{
    Created,
    OperatorProfileNotReady,
    UnknownCity,
}

/// <summary>How the creation ended.</summary>
public sealed class CreateRouteResult
{
    public required CreateRouteOutcome Outcome { get; init; }

    /// <summary>The new route when created.</summary>
    public CreateRouteResponse? Response { get; init; }

    /// <summary>The first stop's city that is not in the city list, when that refused the route.</summary>
    public Guid? UnknownCityId { get; init; }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Routes")]
[Route("routes")]
public sealed class CreateRouteController : ControllerBase
{
    private readonly CreateRouteHandler _handler;
    private readonly CreateRouteValidator _validator = new CreateRouteValidator();

    public CreateRouteController(IWriterConnectionFactory writerFactory)
    {
        _handler = new CreateRouteHandler(writerFactory);
    }

    /// <summary>Create a route (Operator only).</summary>
    /// <remarks>
    /// Creates an active route belonging to the calling operator from an ordered list of 2–12
    /// stops — the first is the source at offset 0, the last the destination — and a distance.
    /// Every stop is a city from the city list; offsets strictly increase, up to 2,880 minutes; no
    /// city appears twice. A route never changes once created. Requires role Operator and the
    /// operator's Fleet profile.
    /// </remarks>
    /// <response code="201">The route was created; Location is its address.</response>
    /// <response code="400">A field failed validation (VALIDATION_FAILED, naming the field in errors).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="422">A stop names a city that is not in the city list (UNKNOWN_CITY, with cityId).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpPost]
    [ProducesResponseType(typeof(CreateRouteResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateRouteAsync(CreateRouteRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        CreateRouteResult result = await _handler.HandleAsync(request, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            case CreateRouteOutcome.Created:
                return Created($"/routes/{result.Response!.RouteId}", result.Response);
            case CreateRouteOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case CreateRouteOutcome.UnknownCity:
                return ProblemResult.UnknownCity(this,
                    $"City {result.UnknownCityId} is not in the city list.", result.UnknownCityId!.Value);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
