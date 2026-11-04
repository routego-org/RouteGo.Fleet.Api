namespace RouteGo.Fleet.Api.Features.Buses.ReplaceBus;

/// <summary>Every detail of the bus, given again in full: its description, seat classes and seat layout.</summary>
public sealed class ReplaceBusRequest
{
    /// <summary>
    /// Letters and digits, with optional spaces and hyphens that are ignored; 6–15 characters once
    /// they are removed. Unique across RouteGo.
    /// </summary>
    public string? RegistrationNumber { get; init; }

    /// <summary>2–80 characters.</summary>
    public string? Name { get; init; }

    /// <summary>Seater, sleeper or semi-sleeper. Locked while the bus has future trips.</summary>
    public BusType? BusType { get; init; }

    /// <summary>Whether the bus is air-conditioned.</summary>
    public bool? IsAc { get; init; }

    /// <summary>Zero or more amenities, each at most once.</summary>
    public IReadOnlyList<Amenity>? Amenities { get; init; }

    /// <summary>1–5 seat class names, 2–30 characters each, unique ignoring case; locked while the bus has future trips.</summary>
    public IReadOnlyList<string>? SeatClasses { get; init; }

    /// <summary>The decks and the seats placed on them; locked while the bus has future trips.</summary>
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

/// <summary>The bus in full, as replaced.</summary>
public sealed class ReplaceBusResponse
{
    public required Guid BusId { get; init; }

    public required string OperatorId { get; init; }

    /// <summary>Upper case, without spaces or hyphens.</summary>
    public required string RegistrationNumber { get; init; }

    public required string Name { get; init; }

    public required BusType BusType { get; init; }

    public required bool IsAc { get; init; }

    public required IReadOnlyList<Amenity> Amenities { get; init; }

    /// <summary>The seat class names in display order.</summary>
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

/// <summary>The future scheduled trips that lock a bus's type, seat classes and layout.</summary>
/// <param name="FutureTripCount">How many scheduled trips have not departed yet.</param>
/// <param name="NextDepartureAt">When the earliest of them departs.</param>
public sealed record BusUse(int FutureTripCount, DateTimeOffset NextDepartureAt);

public enum ReplaceBusOutcome
{
    Replaced,
    OperatorProfileNotReady,
    NotFound,
    RegistrationAlreadyExists,
    BusInUse,
}

/// <summary>How the replacement ended, with the bus when it was replaced or its trips when it is in use.</summary>
/// <param name="Outcome">How the replacement ended.</param>
/// <param name="Response">The bus when replaced.</param>
/// <param name="Use">The future trips when the bus is in use.</param>
public sealed record ReplaceBusResult(ReplaceBusOutcome Outcome, ReplaceBusResponse? Response = null, BusUse? Use = null);

[ApiController]
[ApiExplorerSettings(GroupName = "Buses")]
[Route("buses")]
public sealed class ReplaceBusController : ControllerBase
{
    private readonly ReplaceBusHandler _handler;
    private readonly ReplaceBusValidator _validator = new ReplaceBusValidator();

    public ReplaceBusController(IWriterConnectionFactory writerFactory, IClock clock, PhotoAddresses photoAddresses)
    {
        _handler = new ReplaceBusHandler(writerFactory, clock, photoAddresses);
    }

    /// <summary>Replace a bus's details (Operator only).</summary>
    /// <remarks>
    /// Full replacement of the bus's details. Name, AC, amenities and registration number may always
    /// change. Bus type, seat classes and layout may change only while the bus has no future
    /// scheduled trip; otherwise BUS_IN_USE. Trips already generated keep the values they were
    /// announced with. Requires role Operator and the operator's Fleet profile. A bus that belongs
    /// to another operator is reported as not found.
    /// </remarks>
    /// <param name="busId">The bus's identifier.</param>
    /// <param name="request">Every detail of the bus.</param>
    /// <param name="cancellationToken">Cancels the replacement.</param>
    /// <response code="200">The bus was replaced.</response>
    /// <response code="400">A field failed validation (VALIDATION_FAILED, naming the field in errors).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="404">No bus with this identifier belongs to the caller (NOT_FOUND).</response>
    /// <response code="409">The registration number is already registered (BUS_REGISTRATION_ALREADY_EXISTS), or a locked detail changed while the bus has future trips (BUS_IN_USE).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpPut("{busId}")]
    [ProducesResponseType(typeof(ReplaceBusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReplaceBusAsync(Guid busId, ReplaceBusRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        ReplaceBusResult result = await _handler.HandleAsync(busId, request, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            case ReplaceBusOutcome.Replaced:
                return Ok(result.Response);
            case ReplaceBusOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case ReplaceBusOutcome.NotFound:
                return ProblemResult.NotFound(this, $"You have no bus {busId}.");
            case ReplaceBusOutcome.RegistrationAlreadyExists:
                return ProblemResult.BusRegistrationAlreadyExists(this,
                    $"A bus with registration number {request.RegistrationNumber} is already registered.");
            case ReplaceBusOutcome.BusInUse:
                return ProblemResult.BusInUse(this,
                    "The bus type, seat classes and layout cannot change while the bus has future trips; move or deactivate its schedules first.",
                    result.Use!.FutureTripCount, result.Use.NextDepartureAt);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
