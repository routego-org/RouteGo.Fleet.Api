namespace RouteGo.Fleet.Api.Features.Buses.ListBuses;

/// <summary>Which of the operator's buses to list, and the page wanted.</summary>
public sealed class ListBusesRequest
{
    /// <summary>active or inactive; every bus when absent.</summary>
    public string? Status { get; init; }

    /// <summary>1-based page number; 1 when absent.</summary>
    public int? Page { get; init; }

    /// <summary>Items per page, 1–200; 20 when absent.</summary>
    public int? PageSize { get; init; }
}

/// <summary>One bus as the operator's bus list shows it.</summary>
public sealed class BusSummary
{
    public required Guid BusId { get; init; }

    /// <summary>Letters and digits only, upper case.</summary>
    public required string RegistrationNumber { get; init; }

    public required string Name { get; init; }

    public required BusType BusType { get; init; }

    public required bool IsAc { get; init; }

    public required int TotalSeats { get; init; }

    public required BusStatus Status { get; init; }

    /// <summary>CDN address of the cover photo; null when the bus has no uploaded photo.</summary>
    public required Uri? CoverPhotoUrl { get; init; }
}

/// <summary>One page of the operator's buses, sorted by name.</summary>
public sealed class ListBusesResponse
{
    public required IReadOnlyList<BusSummary> Items { get; init; }

    /// <summary>How many buses match across all pages.</summary>
    public required int TotalCount { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }
}

public enum ListBusesOutcome
{
    Listed,
    OperatorProfileNotReady,
}

/// <summary>How the listing ended, with the page when it was listed.</summary>
/// <param name="Outcome">How the listing ended.</param>
/// <param name="Response">The page when listed.</param>
public sealed record ListBusesResult(ListBusesOutcome Outcome, ListBusesResponse? Response = null);

[ApiController]
[ApiExplorerSettings(GroupName = "Buses")]
[Route("buses")]
public sealed class ListBusesController : ControllerBase
{
    private readonly ListBusesHandler _handler;
    private readonly ListBusesValidator _validator = new ListBusesValidator();

    public ListBusesController(IReaderConnectionFactory readerFactory, PhotoAddresses photoAddresses)
    {
        _handler = new ListBusesHandler(readerFactory, photoAddresses);
    }

    /// <summary>List the operator's own buses (Operator only).</summary>
    /// <remarks>
    /// Lists the calling operator's buses, sorted by name, optionally filtered by status. Requires
    /// role Operator and the operator's Fleet profile. No buses is an empty page, not an error.
    /// </remarks>
    /// <param name="request">The status filter and the page wanted.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <response code="200">The requested page.</response>
    /// <response code="400">The status, page or page size is invalid (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpGet]
    [ProducesResponseType(typeof(ListBusesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListBusesAsync([FromQuery] ListBusesRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        ListBusesResult result = await _handler.HandleAsync(request, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            case ListBusesOutcome.Listed:
                return Ok(result.Response);
            case ListBusesOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
