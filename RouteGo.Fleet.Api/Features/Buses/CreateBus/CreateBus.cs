namespace RouteGo.Fleet.Api.Features.Buses.CreateBus;

/// <summary>The bus to add: its description, its seat classes and its seat layout.</summary>
public sealed class CreateBusRequest
{
    /// <summary>
    /// Letters and digits, with optional spaces and hyphens that are ignored; 6–15 characters once
    /// they are removed. Unique across RouteGo.
    /// </summary>
    public string? RegistrationNumber { get; init; }

    /// <summary>2–80 characters.</summary>
    public string? Name { get; init; }

    /// <summary>Seater, sleeper or semi-sleeper. Descriptive only.</summary>
    public BusType? BusType { get; init; }

    /// <summary>Whether the bus is air-conditioned.</summary>
    public bool? IsAc { get; init; }

    /// <summary>Zero or more amenities, each at most once.</summary>
    public IReadOnlyList<Amenity>? Amenities { get; init; }

    /// <summary>1–5 seat class names, 2–30 characters each, unique ignoring case; every class needs at least one seat.</summary>
    public IReadOnlyList<string>? SeatClasses { get; init; }

    /// <summary>The decks and the seats placed on them.</summary>
    public SeatLayoutInput? Layout { get; init; }
}

/// <summary>One deck (lower), or two (lower and upper), and 1–60 seats placed on them.</summary>
public sealed class SeatLayoutInput
{
    /// <summary>The bus's decks.</summary>
    public IReadOnlyList<SeatLayoutDeckInput>? Decks { get; init; }

    /// <summary>Every seat on the bus.</summary>
    public IReadOnlyList<SeatLayoutSeatInput>? Seats { get; init; }
}

/// <summary>One deck's grid.</summary>
public sealed class SeatLayoutDeckInput
{
    /// <summary>Lower or upper.</summary>
    public Deck? Deck { get; init; }

    /// <summary>1–15.</summary>
    public int? Rows { get; init; }

    /// <summary>1–6.</summary>
    public int? Columns { get; init; }

    /// <summary>The column after which the aisle runs, less than <c>columns</c>; null when the deck has no aisle.</summary>
    public int? AisleAfterColumn { get; init; }
}

/// <summary>One seat and where it is.</summary>
public sealed class SeatLayoutSeatInput
{
    /// <summary>The seat number: 1–5 letters or digits, unique within the bus ignoring case.</summary>
    public string? Label { get; init; }

    /// <summary>One of the bus's decks.</summary>
    public Deck? Deck { get; init; }

    /// <summary>From 1 to the deck's rows.</summary>
    public int? Row { get; init; }

    /// <summary>From 1 to the deck's columns.</summary>
    public int? Column { get; init; }

    /// <summary>One of the bus's seat classes, matched ignoring case.</summary>
    public string? SeatClass { get; init; }
}

/// <summary>The bus as created.</summary>
public sealed class CreateBusResponse
{
    public required Guid BusId { get; init; }

    public required string OperatorId { get; init; }

    /// <summary>Upper case, without spaces or hyphens.</summary>
    public required string RegistrationNumber { get; init; }

    public required string Name { get; init; }

    public required BusType BusType { get; init; }

    public required bool IsAc { get; init; }

    public required IReadOnlyList<Amenity> Amenities { get; init; }

    /// <summary>The seat class names, trimmed, in the order given.</summary>
    public required IReadOnlyList<string> SeatClasses { get; init; }

    public required SeatLayout Layout { get; init; }

    public required int TotalSeats { get; init; }

    public required BusStatus Status { get; init; }

    /// <summary>The bus's uploaded photos in order; a new bus has none.</summary>
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

    /// <summary>The declared size while pending; the stored file's size once uploaded.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>The photo's place in the bus's photo order (1 = cover photo); null while pending.</summary>
    public required int? Position { get; init; }

    /// <summary>The photo's CDN address; null while pending.</summary>
    public required string? Url { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset? UploadedAt { get; init; }
}

public enum CreateBusOutcome
{
    Created,
    OperatorProfileNotReady,
    RegistrationAlreadyExists,
}

/// <summary>How the creation ended, with the bus when it was created.</summary>
/// <param name="Outcome">How the creation ended.</param>
/// <param name="Response">The new bus when created.</param>
public sealed record CreateBusResult(CreateBusOutcome Outcome, CreateBusResponse? Response = null);

[ApiController]
[ApiExplorerSettings(GroupName = "Buses")]
[Route("buses")]
public sealed class CreateBusController : ControllerBase
{
    private readonly CreateBusHandler _handler;
    private readonly CreateBusValidator _validator = new CreateBusValidator();

    public CreateBusController(IWriterConnectionFactory writerFactory)
    {
        _handler = new CreateBusHandler(writerFactory);
    }

    /// <summary>Add a bus (Operator only).</summary>
    /// <remarks>
    /// Creates an active bus belonging to the calling operator, with its seat classes and seat
    /// layout. The registration number must be unique across RouteGo, ignoring case, spaces and
    /// hyphens. Requires role Operator and the operator's Fleet profile.
    /// </remarks>
    /// <response code="201">The bus was created; Location is its address.</response>
    /// <response code="400">A field failed validation (VALIDATION_FAILED, naming the field in errors).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="409">The registration number is already registered (BUS_REGISTRATION_ALREADY_EXISTS).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpPost]
    [ProducesResponseType(typeof(CreateBusResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateBusAsync(CreateBusRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        CreateBusResult result = await _handler.HandleAsync(request, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            case CreateBusOutcome.Created:
                return Created($"/buses/{result.Response!.BusId}", result.Response);
            case CreateBusOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case CreateBusOutcome.RegistrationAlreadyExists:
                return ProblemResult.BusRegistrationAlreadyExists(this,
                    $"A bus with registration number {request.RegistrationNumber} is already registered.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
