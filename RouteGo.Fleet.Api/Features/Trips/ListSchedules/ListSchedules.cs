namespace RouteGo.Fleet.Api.Features.Trips.ListSchedules;

/// <summary>Which of the operator's schedules to list, and the page wanted.</summary>
public sealed class ListSchedulesRequest
{
    /// <summary>active or inactive; every schedule when absent.</summary>
    public string? Status { get; init; }

    // The ids arrive as text so a malformed one is refused by the validator under the field name the
    // contract uses; model binding would name the C# property instead.

    /// <summary>Only schedules on this route (a UUID), when given.</summary>
    public string? RouteId { get; init; }

    /// <summary>Only schedules of this bus (a UUID), when given.</summary>
    public string? BusId { get; init; }

    /// <summary>1-based page number; 1 when absent.</summary>
    public int? Page { get; init; }

    /// <summary>Items per page, 1–200; 20 when absent.</summary>
    public int? PageSize { get; init; }
}

/// <summary>What a seat of one seat class costs on the schedule's trips.</summary>
/// <param name="SeatClass">The seat class, spelt as the bus spells it.</param>
/// <param name="Fare">Whole Indian rupees (INR).</param>
public sealed record ClassFare(string SeatClass, int Fare);

/// <summary>One of the operator's schedules.</summary>
public sealed class Schedule
{
    public required Guid ScheduleId { get; init; }

    public required string OperatorId { get; init; }

    public required Guid RouteId { get; init; }

    public required Guid BusId { get; init; }

    /// <summary>HH:mm in IST.</summary>
    public required string DepartureTime { get; init; }

    /// <summary>The days of service, Monday first.</summary>
    public required IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; }

    /// <summary>One fare per seat class, in the bus's seat-class order.</summary>
    public required IReadOnlyList<ClassFare> ClassFares { get; init; }

    public required ScheduleStatus Status { get; init; }

    /// <summary>The last service date (IST) up to which trips have been generated.</summary>
    public required DateOnly TripsGeneratedThrough { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One page of the operator's schedules, sorted by departure time, then creation time.</summary>
public sealed class ListSchedulesResponse
{
    public required IReadOnlyList<Schedule> Items { get; init; }

    /// <summary>How many schedules match across all pages.</summary>
    public required int TotalCount { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }
}

public enum ListSchedulesOutcome
{
    Listed,
    OperatorProfileNotReady,
}

/// <summary>How the listing ended, with the page when it was listed.</summary>
/// <param name="Outcome">How the listing ended.</param>
/// <param name="Response">The page when listed.</param>
public sealed record ListSchedulesResult(ListSchedulesOutcome Outcome, ListSchedulesResponse? Response = null);

public sealed class ListSchedulesValidator : AbstractValidator<ListSchedulesRequest>
{
    public ListSchedulesValidator()
    {
        RuleFor(x => x.Status).Must(IsScheduleStatus).When(x => x.Status != null)
            .WithMessage("The status must be active or inactive.");
        RuleFor(x => x.RouteId).Must(IsUuid).When(x => x.RouteId != null)
            .WithMessage("The route must be a UUID.");
        RuleFor(x => x.BusId).Must(IsUuid).When(x => x.BusId != null)
            .WithMessage("The bus must be a UUID.");
        RuleFor(x => x.Page).Must(Paging.IsAcceptablePage).WithMessage(Paging.PageMessage);
        RuleFor(x => x.PageSize).Must(Paging.IsAcceptablePageSize).WithMessage(Paging.PageSizeMessage);
    }

    private static bool IsScheduleStatus(string? status)
    {
        return WireJson.TryReadEnum<ScheduleStatus>(status, out _);
    }

    private static bool IsUuid(string? value)
    {
        return Guid.TryParse(value, out _);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Trips")]
[Route("schedules")]
public sealed class ListSchedulesController : ControllerBase
{
    private readonly ListSchedulesHandler _handler;
    private readonly ListSchedulesValidator _validator = new ListSchedulesValidator();

    public ListSchedulesController(IReaderConnectionFactory readerFactory)
    {
        _handler = new ListSchedulesHandler(readerFactory);
    }

    /// <summary>List the operator's own schedules (Operator only).</summary>
    /// <remarks>
    /// Lists the calling operator's schedules, sorted by departure time then creation time,
    /// optionally filtered by status, route and bus. Requires role Operator and the operator's Fleet
    /// profile. No schedules is an empty page, not an error.
    /// </remarks>
    /// <param name="request">The filters and the page wanted.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <response code="200">The requested page.</response>
    /// <response code="400">A filter, the page or the page size is invalid (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpGet]
    [ProducesResponseType(typeof(ListSchedulesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListSchedulesAsync([FromQuery] ListSchedulesRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        ListSchedulesResult result = await _handler.HandleAsync(request, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            case ListSchedulesOutcome.Listed:
                return Ok(result.Response);
            case ListSchedulesOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
