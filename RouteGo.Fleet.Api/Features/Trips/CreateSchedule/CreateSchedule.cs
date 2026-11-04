namespace RouteGo.Fleet.Api.Features.Trips.CreateSchedule;

/// <summary>The schedule to create: a bus on a route, departing at a time of day on chosen days, with a fare for every seat class.</summary>
public sealed class CreateScheduleRequest
{
    /// <summary>One of the operator's active routes.</summary>
    public Guid? RouteId { get; init; }

    /// <summary>One of the operator's active buses.</summary>
    public Guid? BusId { get; init; }

    /// <summary>Departure time of day from the source, HH:mm on the 24-hour clock, in IST.</summary>
    public string? DepartureTime { get; init; }

    /// <summary>1 to 7 different days of the week.</summary>
    public IReadOnlyList<DayOfWeek>? DaysOfWeek { get; init; }

    /// <summary>Exactly one fare for every seat class of the bus.</summary>
    public IReadOnlyList<ClassFareInput>? ClassFares { get; init; }
}

/// <summary>What a seat of one seat class costs.</summary>
public sealed class ClassFareInput
{
    /// <summary>One of the bus's seat classes, matched ignoring case.</summary>
    public string? SeatClass { get; init; }

    /// <summary>Whole Indian rupees (INR), 50 to 10,000. Sent as a number so a fraction can be refused by name.</summary>
    public decimal? Fare { get; init; }
}

/// <summary>What a seat of one seat class costs on the schedule's trips.</summary>
/// <param name="SeatClass">The seat class, spelt as the bus spells it.</param>
/// <param name="Fare">Whole Indian rupees (INR).</param>
public sealed record ClassFare(string SeatClass, int Fare);

/// <summary>The schedule as created.</summary>
public sealed class CreateScheduleResponse
{
    public required Guid ScheduleId { get; init; }

    public required string OperatorId { get; init; }

    public required Guid RouteId { get; init; }

    public required Guid BusId { get; init; }

    /// <summary>HH:mm in IST.</summary>
    public required string DepartureTime { get; init; }

    /// <summary>The days of service, Monday first.</summary>
    public required IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; }

    /// <summary>One fare for every seat class of the bus, in the bus's seat-class order.</summary>
    public required IReadOnlyList<ClassFare> ClassFares { get; init; }

    public required ScheduleStatus Status { get; init; }

    /// <summary>The last service date (IST) up to which trips have been generated.</summary>
    public required DateOnly TripsGeneratedThrough { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

public enum CreateScheduleOutcome
{
    Created,
    OperatorProfileNotReady,
    UnknownRoute,
    RouteInactive,
    UnknownBus,
    BusInactive,
    ClassFaresMismatch,
    BusTripOverlap,
}

/// <summary>How the creation ended.</summary>
public sealed class CreateScheduleResult
{
    public required CreateScheduleOutcome Outcome { get; init; }

    /// <summary>The new schedule when created.</summary>
    public CreateScheduleResponse? Response { get; init; }

    /// <summary>The bus's seat classes the fares left out, when the fares did not match them.</summary>
    public IReadOnlyList<string> MissingClasses { get; init; } = Array.Empty<string>();

    /// <summary>The fares' classes the bus does not have, when the fares did not match them.</summary>
    public IReadOnlyList<string> UnknownClasses { get; init; } = Array.Empty<string>();

    /// <summary>Every generated trip that would have overlapped another trip of the bus.</summary>
    public IReadOnlyList<TripConflict> Conflicts { get; init; } = Array.Empty<TripConflict>();
}

[ApiController]
[ApiExplorerSettings(GroupName = "Trips")]
[Route("schedules")]
public sealed class CreateScheduleController : ControllerBase
{
    private readonly CreateScheduleHandler _handler;
    private readonly CreateScheduleValidator _validator = new CreateScheduleValidator();

    public CreateScheduleController(IWriterConnectionFactory writerFactory, IClock clock)
    {
        _handler = new CreateScheduleHandler(writerFactory, clock);
    }

    /// <summary>Create a schedule and generate its first trips (Operator only).</summary>
    /// <remarks>
    /// Creates an active schedule putting one of the operator's active buses on one of its active
    /// routes, departing at a time of day (IST) on the given days, with a fare for every seat class
    /// of the bus. Generates the schedule's trips for the next 14 days starting today, recording a
    /// TripScheduled event for each, all in one atomic step. Refused whole with BUS_TRIP_OVERLAP if
    /// any trip would overlap another trip of the bus. Requires role Operator and the operator's
    /// Fleet profile.
    /// </remarks>
    /// <param name="request">The schedule to create.</param>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <response code="201">The schedule was created and its trips generated; Location is its address.</response>
    /// <response code="400">A field failed validation (VALIDATION_FAILED, naming the field in errors).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="409">A generated trip would overlap another trip of the bus (BUS_TRIP_OVERLAP, with conflicts).</response>
    /// <response code="422">The bus or route is unknown or inactive (UNKNOWN_BUS, UNKNOWN_ROUTE, BUS_INACTIVE, ROUTE_INACTIVE), or the fares do not match the bus's seat classes (CLASS_FARES_MISMATCH).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpPost]
    [ProducesResponseType(typeof(CreateScheduleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateScheduleAsync(CreateScheduleRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        string correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        CreateScheduleResult result = await _handler.HandleAsync(request, operatorId, correlationId, cancellationToken);
        return ToActionResult(request, result);
    }

    private IActionResult ToActionResult(CreateScheduleRequest request, CreateScheduleResult result)
    {
        Guid routeId = request.RouteId!.Value;
        Guid busId = request.BusId!.Value;
        switch (result.Outcome)
        {
            case CreateScheduleOutcome.Created:
                return Created($"/schedules/{result.Response!.ScheduleId}", result.Response);
            case CreateScheduleOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case CreateScheduleOutcome.UnknownRoute:
                return ProblemResult.UnknownRoute(this, $"You have no route {routeId}.", routeId);
            case CreateScheduleOutcome.RouteInactive:
                return ProblemResult.RouteInactive(this, $"Route {routeId} is inactive; choose an active route.", routeId);
            case CreateScheduleOutcome.UnknownBus:
                return ProblemResult.UnknownBus(this, $"You have no bus {busId}.", busId);
            case CreateScheduleOutcome.BusInactive:
                return ProblemResult.BusInactive(this, $"Bus {busId} is inactive; activate it or choose another bus.", busId);
            case CreateScheduleOutcome.ClassFaresMismatch:
                return ProblemResult.ClassFaresMismatch(this, "The fares must name exactly the bus's seat classes.",
                    result.MissingClasses, result.UnknownClasses);
            case CreateScheduleOutcome.BusTripOverlap:
                return ProblemResult.BusTripOverlap(this,
                    $"{result.Conflicts.Count} of the schedule's trips would overlap another trip of the bus.", result.Conflicts);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
