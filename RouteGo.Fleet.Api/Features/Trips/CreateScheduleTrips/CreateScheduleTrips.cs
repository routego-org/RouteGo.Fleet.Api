using System.Globalization;

namespace RouteGo.Fleet.Api.Features.Trips.CreateScheduleTrips;

/// <summary>How far ahead to generate the schedule's trips.</summary>
public sealed class CreateScheduleTripsRequest
{
    /// <summary>
    /// The last service date (IST) to generate trips for, YYYY-MM-DD; from today up to today plus
    /// 60 days. Sent as text so a malformed date is refused under this field's name.
    /// </summary>
    public string? EndDate { get; init; }
}

/// <summary>What a seat of one seat class costs on the trip.</summary>
/// <param name="SeatClass">The seat class, spelt as the bus spells it.</param>
/// <param name="Fare">Whole Indian rupees (INR).</param>
public sealed record ClassFare(string SeatClass, int Fare);

/// <summary>A city a trip starts or ends at.</summary>
/// <param name="CityId">The city's identifier.</param>
/// <param name="Name">The city's name.</param>
public sealed record TripCity(Guid CityId, string Name);

/// <summary>One generated trip.</summary>
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

    public required TripStatus Status { get; init; }

    /// <summary>One fare for every seat class of the bus, in the bus's seat-class order.</summary>
    public required IReadOnlyList<ClassFare> ClassFares { get; init; }

    /// <summary>Null: a generated trip is not cancelled.</summary>
    public required string? CancelReason { get; init; }

    /// <summary>Null: a generated trip is not cancelled.</summary>
    public required DateTimeOffset? CancelledAt { get; init; }
}

/// <summary>The trips this request generated, and how far the schedule's trips now reach.</summary>
public sealed class CreateScheduleTripsResponse
{
    public required Guid ScheduleId { get; init; }

    /// <summary>The last service date (IST) up to which trips have been generated.</summary>
    public required DateOnly TripsGeneratedThrough { get; init; }

    /// <summary>The trips this request generated, in departure order; empty when there was nothing to generate.</summary>
    public required IReadOnlyList<Trip> Items { get; init; }
}

public enum CreateScheduleTripsOutcome
{
    Created,
    OperatorProfileNotReady,
    NotFound,
    ScheduleInactive,
    BusInactive,
    EndDateOutOfRange,
    ClassFaresMismatch,
    BusTripOverlap,
}

/// <summary>How the extension ended.</summary>
public sealed class CreateScheduleTripsResult
{
    public required CreateScheduleTripsOutcome Outcome { get; init; }

    /// <summary>The generated trips when created.</summary>
    public CreateScheduleTripsResponse? Response { get; init; }

    /// <summary>The schedule's bus, when it is inactive.</summary>
    public Guid BusId { get; init; }

    /// <summary>The latest end date allowed today, when the end date is out of range.</summary>
    public DateOnly LatestAllowedDate { get; init; }

    /// <summary>The bus's seat classes the schedule has no fare for, when its fares no longer match the bus.</summary>
    public IReadOnlyList<string> MissingClasses { get; init; } = Array.Empty<string>();

    /// <summary>The schedule's fare classes the bus no longer has, when its fares no longer match the bus.</summary>
    public IReadOnlyList<string> UnknownClasses { get; init; } = Array.Empty<string>();

    /// <summary>Every generated trip that would have overlapped another trip of the bus.</summary>
    public IReadOnlyList<TripConflict> Conflicts { get; init; } = Array.Empty<TripConflict>();
}

/// <summary>Which schedule to extend, for whom, and under which correlation ID its events are recorded.</summary>
/// <param name="ScheduleId">The schedule.</param>
/// <param name="OperatorId">The calling operator; only its schedules can be extended.</param>
/// <param name="CorrelationId">The request's correlation ID, copied into every event.</param>
public sealed record ExtensionTarget(Guid ScheduleId, string OperatorId, string CorrelationId);

public sealed class CreateScheduleTripsValidator : AbstractValidator<CreateScheduleTripsRequest>
{
    /// <summary>The calendar-date format the contract uses.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    public CreateScheduleTripsValidator()
    {
        RuleFor(x => x.EndDate).NotNull().WithMessage("The end date is required.");
        RuleFor(x => x.EndDate).Must(IsDate).When(x => x.EndDate != null)
            .WithMessage("The end date must be a calendar date, YYYY-MM-DD.");
    }

    private static bool IsDate(string? endDate)
    {
        return DateOnly.TryParseExact(endDate, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Trips")]
[Route("schedules")]
public sealed class CreateScheduleTripsController : ControllerBase
{
    private readonly CreateScheduleTripsHandler _handler;
    private readonly CreateScheduleTripsValidator _validator = new CreateScheduleTripsValidator();

    public CreateScheduleTripsController(IWriterConnectionFactory writerFactory, IClock clock)
    {
        _handler = new CreateScheduleTripsHandler(writerFactory, clock);
    }

    /// <summary>Generate a schedule's trips up to an end date (Operator only).</summary>
    /// <remarks>
    /// Generates the schedule's trips for every day of service from today through the end date (IST,
    /// at most 60 days ahead) that has no scheduled trip, was not cancelled by the operator, and
    /// departs in the future; records TripScheduled for each, all in one atomic step. Moves the
    /// generated-through date forward. Extending again to the same date generates nothing. Requires
    /// role Operator and the operator's Fleet profile. A schedule that belongs to another operator is
    /// reported as not found.
    /// </remarks>
    /// <param name="scheduleId">The schedule's identifier.</param>
    /// <param name="request">The end date.</param>
    /// <param name="cancellationToken">Cancels the extension.</param>
    /// <response code="201">The trips generated by this request (possibly none).</response>
    /// <response code="400">The end date is missing or not a date (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="404">No schedule with this identifier belongs to the caller (NOT_FOUND).</response>
    /// <response code="409">The schedule is inactive (SCHEDULE_INACTIVE), or a generated trip would overlap another trip of the bus (BUS_TRIP_OVERLAP).</response>
    /// <response code="422">The bus is inactive (BUS_INACTIVE), the end date is out of range (TRIP_END_DATE_OUT_OF_RANGE), or the schedule's fares no longer match the bus's seat classes (CLASS_FARES_MISMATCH).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpPost("{scheduleId}/trips")]
    [ProducesResponseType(typeof(CreateScheduleTripsResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateScheduleTripsAsync(
        Guid scheduleId, CreateScheduleTripsRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        string correlationId = CorrelationIdMiddleware.GetCorrelationId(HttpContext);
        var target = new ExtensionTarget(scheduleId, operatorId, correlationId);
        var endDate = DateOnly.ParseExact(request.EndDate!, CreateScheduleTripsValidator.DateFormat, CultureInfo.InvariantCulture);
        CreateScheduleTripsResult result = await _handler.HandleAsync(target, endDate, cancellationToken);
        return ToActionResult(scheduleId, result);
    }

    private IActionResult ToActionResult(Guid scheduleId, CreateScheduleTripsResult result)
    {
        switch (result.Outcome)
        {
            case CreateScheduleTripsOutcome.Created:
                return StatusCode(StatusCodes.Status201Created, result.Response);
            case CreateScheduleTripsOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case CreateScheduleTripsOutcome.NotFound:
                return ProblemResult.NotFound(this, $"You have no schedule {scheduleId}.");
            case CreateScheduleTripsOutcome.ScheduleInactive:
                return ProblemResult.ScheduleInactive(this, $"Schedule {scheduleId} is inactive; activate it before extending it.");
            case CreateScheduleTripsOutcome.BusInactive:
                return ProblemResult.BusInactive(this, $"The schedule's bus {result.BusId} is inactive; activate it first.", result.BusId);
            case CreateScheduleTripsOutcome.EndDateOutOfRange:
                return ProblemResult.TripEndDateOutOfRange(this,
                    $"The end date must be from today to {result.LatestAllowedDate:yyyy-MM-dd}.", result.LatestAllowedDate);
            case CreateScheduleTripsOutcome.ClassFaresMismatch:
                return ProblemResult.ClassFaresMismatch(this,
                    "The schedule's fares no longer match the bus's seat classes; edit the schedule's fares first.",
                    result.MissingClasses, result.UnknownClasses);
            case CreateScheduleTripsOutcome.BusTripOverlap:
                return ProblemResult.BusTripOverlap(this,
                    $"{result.Conflicts.Count} of the new trips would overlap another trip of the bus.", result.Conflicts);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
