namespace RouteGo.Fleet.Api.Features.Trips.ReplaceTripFares;

/// <summary>The trip's new fares: one for every seat class of its bus.</summary>
public sealed class ReplaceTripFaresRequest
{
    /// <summary>Exactly one fare for every seat class of the trip's bus.</summary>
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

/// <summary>What a seat of one seat class costs on the trip.</summary>
/// <param name="SeatClass">The seat class, spelt as the bus spells it.</param>
/// <param name="Fare">Whole Indian rupees (INR).</param>
public sealed record ClassFare(string SeatClass, int Fare);

/// <summary>A city a trip starts or ends at.</summary>
/// <param name="CityId">The city's identifier.</param>
/// <param name="Name">The city's name.</param>
public sealed record TripCity(Guid CityId, string Name);

/// <summary>The trip with its fares.</summary>
public sealed class Trip
{
    public required Guid TripId { get; init; }

    public required Guid ScheduleId { get; init; }

    public required string OperatorId { get; init; }

    /// <summary>The operator's company name.</summary>
    public required string OperatorName { get; init; }

    public required Guid RouteId { get; init; }

    public required TripCity Source { get; init; }

    public required TripCity Destination { get; init; }

    public required Guid BusId { get; init; }

    /// <summary>The bus's current name.</summary>
    public required string BusName { get; init; }

    /// <summary>The calendar date (IST) on which the trip departs.</summary>
    public required DateOnly ServiceDate { get; init; }

    public required DateTimeOffset DepartureAt { get; init; }

    public required DateTimeOffset ArrivalAt { get; init; }

    /// <summary>Always scheduled: only a scheduled trip that has not departed can change its fares.</summary>
    public required TripStatus Status { get; init; }

    /// <summary>One fare for every seat class of the bus, in the bus's seat-class order.</summary>
    public required IReadOnlyList<ClassFare> ClassFares { get; init; }

    /// <summary>Null: a trip whose fares change is not cancelled.</summary>
    public required string? CancelReason { get; init; }

    /// <summary>Null: a trip whose fares change is not cancelled.</summary>
    public required DateTimeOffset? CancelledAt { get; init; }
}

public enum ReplaceTripFaresOutcome
{
    Replaced,
    OperatorProfileNotReady,
    NotFound,
    TripNotScheduled,
    TripDeparted,
    FareChangeCutoffPassed,
    ClassFaresMismatch,
}

/// <summary>How the fare change ended.</summary>
public sealed class ReplaceTripFaresResult
{
    public required ReplaceTripFaresOutcome Outcome { get; init; }

    /// <summary>The trip with its fares when replaced.</summary>
    public Trip? Response { get; init; }

    /// <summary>When the trip departed, when it has.</summary>
    public DateTimeOffset DepartedAt { get; init; }

    /// <summary>When the trip's fares froze, when the cutoff has passed.</summary>
    public DateTimeOffset CutoffAt { get; init; }

    /// <summary>The bus's seat classes the fares left out, when the fares did not match them.</summary>
    public IReadOnlyList<string> MissingClasses { get; init; } = Array.Empty<string>();

    /// <summary>The fares' classes the bus does not have, when the fares did not match them.</summary>
    public IReadOnlyList<string> UnknownClasses { get; init; } = Array.Empty<string>();
}

/// <summary>Which trip to re-price, for whom, and under which correlation ID its event is recorded.</summary>
/// <param name="TripId">The trip.</param>
/// <param name="OperatorId">The calling operator; only its trips can be re-priced.</param>
/// <param name="CorrelationId">The request's correlation ID, copied into the event.</param>
public sealed record TripTarget(Guid TripId, string OperatorId, string CorrelationId);

[ApiController]
[ApiExplorerSettings(GroupName = "Trips")]
[Route("trips")]
public sealed class ReplaceTripFaresController : ControllerBase
{
    private readonly ReplaceTripFaresHandler _handler;
    private readonly ReplaceTripFaresValidator _validator = new ReplaceTripFaresValidator();

    public ReplaceTripFaresController(IWriterConnectionFactory writerFactory, IClock clock)
    {
        _handler = new ReplaceTripFaresHandler(writerFactory, clock);
    }

    /// <summary>Replace one trip's class fares (Operator only).</summary>
    /// <remarks>
    /// Replaces the trip's fare for every seat class of its bus. Allowed while the trip is scheduled
    /// and departs more than 6 hours from now. Records FareChanged, with every seat's new fare, when
    /// any class fare changed. The schedule and its other trips are unchanged. Requires role Operator
    /// and the operator's Fleet profile. A trip that belongs to another operator is reported as not
    /// found.
    /// </remarks>
    /// <param name="tripId">The trip's identifier.</param>
    /// <param name="request">The new fares.</param>
    /// <param name="cancellationToken">Cancels the fare change.</param>
    /// <response code="200">The trip with its fares.</response>
    /// <response code="400">A fare failed validation (VALIDATION_FAILED, naming the field in errors).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="404">No trip with this identifier belongs to the caller (NOT_FOUND).</response>
    /// <response code="409">The trip is cancelled (TRIP_NOT_SCHEDULED) or has departed (TRIP_DEPARTED).</response>
    /// <response code="422">The fares are frozen (FARE_CHANGE_CUTOFF_PASSED), or do not match the bus's seat classes (CLASS_FARES_MISMATCH).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpPut("{tripId}/fares")]
    [ProducesResponseType(typeof(Trip), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ReplaceTripFaresAsync(
        Guid tripId, ReplaceTripFaresRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        string correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var target = new TripTarget(tripId, operatorId, correlationId);
        ReplaceTripFaresResult result = await _handler.HandleAsync(target, request, cancellationToken);
        return ToActionResult(tripId, result);
    }

    private IActionResult ToActionResult(Guid tripId, ReplaceTripFaresResult result)
    {
        switch (result.Outcome)
        {
            case ReplaceTripFaresOutcome.Replaced:
                return Ok(result.Response);
            case ReplaceTripFaresOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case ReplaceTripFaresOutcome.NotFound:
                return ProblemResult.NotFound(this, $"You have no trip {tripId}.");
            case ReplaceTripFaresOutcome.TripNotScheduled:
                return ProblemResult.TripNotScheduled(this, $"Trip {tripId} is cancelled; its fares can no longer change.", TripStatus.Cancelled);
            case ReplaceTripFaresOutcome.TripDeparted:
                return ProblemResult.TripDeparted(this, $"Trip {tripId} has already departed.", result.DepartedAt);
            case ReplaceTripFaresOutcome.FareChangeCutoffPassed:
                return ProblemResult.FareChangeCutoffPassed(this,
                    $"Fares are frozen 6 hours before departure; trip {tripId}'s fares can no longer change.", result.CutoffAt);
            case ReplaceTripFaresOutcome.ClassFaresMismatch:
                return ProblemResult.ClassFaresMismatch(this, "The fares must name exactly the bus's seat classes.",
                    result.MissingClasses, result.UnknownClasses);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
