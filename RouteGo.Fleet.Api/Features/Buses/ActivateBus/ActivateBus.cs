namespace RouteGo.Fleet.Api.Features.Buses.ActivateBus;

/// <summary>The bus in full, now active: its description, seat classes, seat layout and uploaded photos.</summary>
public sealed class ActivateBusResponse
{
    public required Guid BusId { get; init; }

    public required string OperatorId { get; init; }

    /// <summary>Upper case, without spaces or hyphens.</summary>
    public required string RegistrationNumber { get; init; }

    public required string Name { get; init; }

    public required BusType BusType { get; init; }

    public required bool IsAc { get; init; }

    public required IReadOnlyList<Amenity> Amenities { get; init; }

    /// <summary>The seat class names in the order the operator gave them.</summary>
    public required IReadOnlyList<string> SeatClasses { get; init; }

    public required SeatLayout Layout { get; init; }

    public required int TotalSeats { get; init; }

    public required BusStatus Status { get; init; }

    /// <summary>The bus's uploaded photos in order; the first is the cover photo.</summary>
    public required IReadOnlyList<BusPhoto> Photos { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>The bus's decks and seats.</summary>
/// <param name="Decks">One deck (lower), or two (lower and upper).</param>
/// <param name="Seats">Every seat, each naming its seat class exactly as the bus spells it.</param>
public sealed record SeatLayout(IReadOnlyList<SeatLayoutDeck> Decks, IReadOnlyList<SeatLayoutSeat> Seats);

/// <summary>One deck's grid.</summary>
public sealed class SeatLayoutDeck
{
    public required Deck Deck { get; init; }

    public required int Rows { get; init; }

    public required int Columns { get; init; }

    /// <summary>Null when the deck has no aisle.</summary>
    public required int? AisleAfterColumn { get; init; }
}

/// <summary>One seat and where it is.</summary>
public sealed class SeatLayoutSeat
{
    public required string Label { get; init; }

    public required Deck Deck { get; init; }

    public required int Row { get; init; }

    public required int Column { get; init; }

    public required string SeatClass { get; init; }
}

/// <summary>One of the bus's photos.</summary>
public sealed class BusPhoto
{
    public required Guid PhotoId { get; init; }

    public required BusPhotoStatus Status { get; init; }

    public required PhotoFormat Format { get; init; }

    /// <summary>The stored file's size.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>The photo's place in the bus's photo order (1 = cover photo).</summary>
    public required int? Position { get; init; }

    /// <summary>The photo's CDN address.</summary>
    public required Uri? Url { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset? UploadedAt { get; init; }
}

public enum ActivateBusOutcome
{
    Activated,
    OperatorProfileNotReady,
    NotFound,
}

/// <summary>How the activation ended, with the bus when it was found.</summary>
/// <param name="Outcome">How the activation ended.</param>
/// <param name="Response">The active bus when found.</param>
public sealed record ActivateBusResult(ActivateBusOutcome Outcome, ActivateBusResponse? Response = null);

[ApiController]
[ApiExplorerSettings(GroupName = "Buses")]
[Route("buses")]
public sealed class ActivateBusController : ControllerBase
{
    private readonly ActivateBusHandler _handler;

    public ActivateBusController(IWriterConnectionFactory writerFactory, PhotoAddresses photoAddresses)
    {
        _handler = new ActivateBusHandler(writerFactory, photoAddresses);
    }

    /// <summary>Put a bus back in service (Operator only).</summary>
    /// <remarks>
    /// Makes the bus active. Activating an active bus changes nothing. Requires role Operator and the
    /// operator's Fleet profile. A bus that belongs to another operator is reported as not found.
    /// </remarks>
    /// <param name="busId">The bus's identifier.</param>
    /// <param name="cancellationToken">Cancels the activation.</param>
    /// <response code="200">The bus is active.</response>
    /// <response code="400">The bus identifier is not a UUID (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="404">No bus with this identifier belongs to the caller (NOT_FOUND).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpPost("{busId}/activate")]
    [ProducesResponseType(typeof(ActivateBusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ActivateBusAsync(Guid busId, CancellationToken cancellationToken)
    {
        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        ActivateBusResult result = await _handler.HandleAsync(busId, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            case ActivateBusOutcome.Activated:
                return Ok(result.Response);
            case ActivateBusOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case ActivateBusOutcome.NotFound:
                return ProblemResult.NotFound(this, $"You have no bus {busId}.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
