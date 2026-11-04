namespace RouteGo.Fleet.Api.Features.Trips.GetSchedule;

/// <summary>What a seat of one seat class costs on the schedule's trips.</summary>
/// <param name="SeatClass">The seat class, spelt as the bus spells it.</param>
/// <param name="Fare">Whole Indian rupees (INR).</param>
public sealed record ClassFare(string SeatClass, int Fare);

/// <summary>One of the operator's schedules.</summary>
public sealed class GetScheduleResponse
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

public enum GetScheduleOutcome
{
    Found,
    OperatorProfileNotReady,
    NotFound,
}

/// <summary>How the read ended, with the schedule when it was found.</summary>
/// <param name="Outcome">How the read ended.</param>
/// <param name="Response">The schedule when found.</param>
public sealed record GetScheduleResult(GetScheduleOutcome Outcome, GetScheduleResponse? Response = null);

[ApiController]
[ApiExplorerSettings(GroupName = "Trips")]
[Route("schedules")]
public sealed class GetScheduleController : ControllerBase
{
    private readonly GetScheduleHandler _handler;

    public GetScheduleController(IReaderConnectionFactory readerFactory)
    {
        _handler = new GetScheduleHandler(readerFactory);
    }

    /// <summary>Read one of the operator's schedules (Operator only).</summary>
    /// <remarks>
    /// Returns the schedule. Requires role Operator and the operator's Fleet profile. A schedule
    /// that belongs to another operator is reported as not found.
    /// </remarks>
    /// <param name="scheduleId">The schedule's identifier.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <response code="200">The schedule.</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="404">No schedule with this identifier belongs to the caller (NOT_FOUND).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpGet("{scheduleId}")]
    [ProducesResponseType(typeof(GetScheduleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetScheduleAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        GetScheduleResult result = await _handler.HandleAsync(scheduleId, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            case GetScheduleOutcome.Found:
                return Ok(result.Response);
            case GetScheduleOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case GetScheduleOutcome.NotFound:
                return ProblemResult.NotFound(this, $"You have no schedule {scheduleId}.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
