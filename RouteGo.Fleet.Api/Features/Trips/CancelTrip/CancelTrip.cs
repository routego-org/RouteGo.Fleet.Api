namespace RouteGo.Fleet.Api.Features.Trips.CancelTrip;

/// <summary>Why the trip is cancelled.</summary>
public sealed class CancelTripRequest
{
    /// <summary>10 to 500 characters; carried in TripCancelled and kept with the trip.</summary>
    public string? Reason { get; init; }
}

/// <summary>What a seat of one seat class costs on the trip.</summary>
/// <param name="SeatClass">The seat class, spelt as the bus spells it.</param>
/// <param name="Fare">Whole Indian rupees (INR).</param>
public sealed record ClassFare(string SeatClass, int Fare);

/// <summary>A city a trip starts or ends at.</summary>
/// <param name="CityId">The city's identifier.</param>
/// <param name="Name">The city's name.</param>
public sealed record TripCity(Guid CityId, string Name);

/// <summary>The cancelled trip.</summary>
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

    /// <summary>The calendar date (IST) on which the trip would have departed.</summary>
    public required DateOnly ServiceDate { get; init; }

    public required DateTimeOffset DepartureAt { get; init; }

    public required DateTimeOffset ArrivalAt { get; init; }

    /// <summary>Always cancelled.</summary>
    public required TripStatus Status { get; init; }

    /// <summary>One fare for every seat class of the bus, in the bus's seat-class order.</summary>
    public required IReadOnlyList<ClassFare> ClassFares { get; init; }

    /// <summary>The operator's reason.</summary>
    public required string? CancelReason { get; init; }

    /// <summary>When the trip was cancelled.</summary>
    public required DateTimeOffset? CancelledAt { get; init; }
}

public enum CancelTripOutcome
{
    Cancelled,
    OperatorProfileNotReady,
    NotFound,
    TripNotScheduled,
    TripDeparted,
}

/// <summary>How the cancellation ended.</summary>
public sealed class CancelTripResult
{
    public required CancelTripOutcome Outcome { get; init; }

    /// <summary>The cancelled trip when cancelled.</summary>
    public Trip? Response { get; init; }

    /// <summary>When the trip departed, when it has.</summary>
    public DateTimeOffset DepartedAt { get; init; }
}

/// <summary>Which trip to cancel, for whom, and under which correlation ID its event is recorded.</summary>
/// <param name="TripId">The trip.</param>
/// <param name="OperatorId">The calling operator; only its trips can be cancelled.</param>
/// <param name="CorrelationId">The request's correlation ID, copied into the event.</param>
public sealed record TripTarget(Guid TripId, string OperatorId, string CorrelationId);

public sealed class CancelTripValidator : AbstractValidator<CancelTripRequest>
{
    public CancelTripValidator()
    {
        RuleFor(x => x.Reason).Must(TripRules.IsAcceptableCancelReason)
            .WithMessage("The reason must be 10 to 500 characters.");
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Trips")]
[Route("trips")]
public sealed class CancelTripController : ControllerBase
{
    private readonly CancelTripHandler _handler;
    private readonly CancelTripValidator _validator = new CancelTripValidator();

    public CancelTripController(IWriterConnectionFactory writerFactory, IClock clock)
    {
        _handler = new CancelTripHandler(writerFactory, clock);
    }

    /// <summary>Cancel one trip with a reason (Operator only).</summary>
    /// <remarks>
    /// Cancels a scheduled trip that has not departed, with a reason of 10–500 characters, and records
    /// TripCancelled. Cancellation is final; the trip's service date is never given a new trip by its
    /// schedule. Requires role Operator and the operator's Fleet profile. A trip that belongs to
    /// another operator is reported as not found.
    /// </remarks>
    /// <param name="tripId">The trip's identifier.</param>
    /// <param name="request">The reason.</param>
    /// <param name="cancellationToken">Cancels the request (not the trip).</param>
    /// <response code="200">The trip is cancelled.</response>
    /// <response code="400">The reason is missing or not 10–500 characters (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="404">No trip with this identifier belongs to the caller (NOT_FOUND).</response>
    /// <response code="409">The trip is already cancelled (TRIP_NOT_SCHEDULED) or has departed (TRIP_DEPARTED).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpPost("{tripId}/cancel")]
    [ProducesResponseType(typeof(Trip), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelTripAsync(Guid tripId, CancelTripRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        string correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var target = new TripTarget(tripId, operatorId, correlationId);
        CancelTripResult result = await _handler.HandleAsync(target, request.Reason!, cancellationToken);
        switch (result.Outcome)
        {
            case CancelTripOutcome.Cancelled:
                return Ok(result.Response);
            case CancelTripOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case CancelTripOutcome.NotFound:
                return ProblemResult.NotFound(this, $"You have no trip {tripId}.");
            case CancelTripOutcome.TripNotScheduled:
                return ProblemResult.TripNotScheduled(this, $"Trip {tripId} is already cancelled.", TripStatus.Cancelled);
            case CancelTripOutcome.TripDeparted:
                return ProblemResult.TripDeparted(this, $"Trip {tripId} has already departed.", result.DepartedAt);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
