namespace RouteGo.Fleet.Api.Features.Buses.ReplaceBusPhotoOrder;

/// <summary>The bus's uploaded photos in their new order.</summary>
public sealed class ReplaceBusPhotoOrderRequest
{
    /// <summary>Every uploaded photo of the bus, exactly once; the first becomes the cover photo.</summary>
    public IReadOnlyList<Guid>? PhotoIds { get; init; }
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

/// <summary>The bus's photos in their new order.</summary>
/// <param name="Items">The uploaded photos, the cover photo first.</param>
public sealed record ReplaceBusPhotoOrderResponse(IReadOnlyList<BusPhoto> Items);

public enum ReplaceBusPhotoOrderOutcome
{
    Replaced,
    OperatorProfileNotReady,
    NotFound,
    NotTheUploadedPhotos,
}

/// <summary>How the reorder ended, with the photos when it was applied.</summary>
/// <param name="Outcome">How the reorder ended.</param>
/// <param name="Response">The photos in their new order when applied.</param>
public sealed record ReplaceBusPhotoOrderResult(ReplaceBusPhotoOrderOutcome Outcome, ReplaceBusPhotoOrderResponse? Response = null);

public sealed class ReplaceBusPhotoOrderValidator : AbstractValidator<ReplaceBusPhotoOrderRequest>
{
    public ReplaceBusPhotoOrderValidator()
    {
        RuleFor(x => x.PhotoIds).NotNull().WithMessage("The photo list is required.");
        RuleFor(x => x.PhotoIds!).Must(HaveAcceptableCount).When(x => x.PhotoIds != null)
            .WithMessage("The photo list names 1 to 5 photos.");
        RuleFor(x => x.PhotoIds!).Must(HaveNoRepeats).When(x => x.PhotoIds != null)
            .WithMessage("Each photo may be listed only once.");
    }

    private static bool HaveAcceptableCount(IReadOnlyList<Guid> photoIds)
    {
        return photoIds.Count >= 1 && photoIds.Count <= BusPhotoRules.MaxPhotosPerBus;
    }

    private static bool HaveNoRepeats(IReadOnlyList<Guid> photoIds)
    {
        return photoIds.Distinct().Count() == photoIds.Count;
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Buses")]
[Route("buses")]
public sealed class ReplaceBusPhotoOrderController : ControllerBase
{
    private const string PhotoIdsField = "photoIds";

    private readonly ReplaceBusPhotoOrderHandler _handler;
    private readonly ReplaceBusPhotoOrderValidator _validator = new ReplaceBusPhotoOrderValidator();

    public ReplaceBusPhotoOrderController(IWriterConnectionFactory writerFactory, PhotoAddresses photoAddresses)
    {
        _handler = new ReplaceBusPhotoOrderHandler(writerFactory, photoAddresses);
    }

    /// <summary>Set the order of a bus's photos (Operator only).</summary>
    /// <remarks>
    /// Replaces the order of the bus's uploaded photos; the list must name every uploaded photo
    /// exactly once. The first becomes the cover photo. Requires role Operator and the operator's
    /// Fleet profile. A bus that belongs to another operator is reported as not found.
    /// </remarks>
    /// <param name="busId">The bus's identifier.</param>
    /// <param name="request">The uploaded photos in their new order.</param>
    /// <param name="cancellationToken">Cancels the reorder.</param>
    /// <response code="200">The photos in their new order.</response>
    /// <response code="400">The list does not name every uploaded photo exactly once (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="404">No bus with this identifier belongs to the caller (NOT_FOUND).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpPut("{busId}/photoOrder")]
    [ProducesResponseType(typeof(ReplaceBusPhotoOrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReplaceBusPhotoOrderAsync(
        Guid busId, ReplaceBusPhotoOrderRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        ReplaceBusPhotoOrderResult result = await _handler.HandleAsync(busId, request.PhotoIds!, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            case ReplaceBusPhotoOrderOutcome.Replaced:
                return Ok(result.Response);
            case ReplaceBusPhotoOrderOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case ReplaceBusPhotoOrderOutcome.NotFound:
                return ProblemResult.NotFound(this, $"You have no bus {busId}.");
            // Which photos are uploaded is only known from the database, but to the caller this is
            // the same field error as a malformed list, so it is reported the same way.
            case ReplaceBusPhotoOrderOutcome.NotTheUploadedPhotos:
                ModelState.AddModelError(PhotoIdsField, "The list must name every uploaded photo of the bus exactly once.");
                return ValidationProblem(ModelState);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
