namespace RouteGo.Fleet.Api.Features.Buses.ConfirmBusPhoto;

/// <summary>The photo, now uploaded and in the bus's photo order.</summary>
public sealed class ConfirmBusPhotoResponse
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

public enum ConfirmBusPhotoOutcome
{
    Confirmed,
    OperatorProfileNotReady,
    NotFound,
    UploadExpired,
    NotUploaded,
    Invalid,
}

/// <summary>How the confirmation ended, with what each outcome reports.</summary>
public sealed class ConfirmBusPhotoResult
{
    public required ConfirmBusPhotoOutcome Outcome { get; init; }

    /// <summary>The uploaded photo when confirmed.</summary>
    public ConfirmBusPhotoResponse? Response { get; init; }

    /// <summary>When the slot expired, when it had.</summary>
    public DateTimeOffset? ExpiredAt { get; init; }

    /// <summary>What is wrong with the stored file, when it is invalid.</summary>
    public PhotoFileFault? Fault { get; init; }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Buses")]
[Route("buses")]
public sealed class ConfirmBusPhotoController : ControllerBase
{
    private readonly ConfirmBusPhotoHandler _handler;

    public ConfirmBusPhotoController(
        IWriterConnectionFactory writerFactory, IClock clock, IMediaStore mediaStore, PhotoAddresses photoAddresses)
    {
        _handler = new ConfirmBusPhotoHandler(writerFactory, clock, mediaStore, photoAddresses);
    }

    /// <summary>Check an uploaded file and add it to the bus's photos (Operator only).</summary>
    /// <remarks>
    /// Checks the file stored for a pending photo — it must exist, have the declared format, and be
    /// no larger than 3 MB — then marks the photo uploaded and puts it last in the bus's photo order.
    /// Confirming an uploaded photo changes nothing. An invalid file is removed and the slot
    /// discarded. Requires role Operator and the operator's Fleet profile. A bus that belongs to
    /// another operator is reported as not found.
    /// </remarks>
    /// <param name="busId">The bus's identifier.</param>
    /// <param name="photoId">The photo's identifier.</param>
    /// <param name="cancellationToken">Cancels the confirmation.</param>
    /// <response code="200">The photo is uploaded.</response>
    /// <response code="400">An identifier is not a UUID (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="404">No such photo on a bus of the caller's (NOT_FOUND).</response>
    /// <response code="422">The slot expired (PHOTO_UPLOAD_EXPIRED), the file has not arrived (PHOTO_NOT_UPLOADED), or the file breaks the photo rules (PHOTO_INVALID).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpPost("{busId}/photos/{photoId}/confirm")]
    [ProducesResponseType(typeof(ConfirmBusPhotoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ConfirmBusPhotoAsync(Guid busId, Guid photoId, CancellationToken cancellationToken)
    {
        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        ConfirmBusPhotoResult result = await _handler.HandleAsync(busId, photoId, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            case ConfirmBusPhotoOutcome.Confirmed:
                return Ok(result.Response);
            case ConfirmBusPhotoOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case ConfirmBusPhotoOutcome.NotFound:
                return ProblemResult.NotFound(this, $"Your bus {busId} has no photo {photoId}.");
            case ConfirmBusPhotoOutcome.UploadExpired:
                return ProblemResult.PhotoUploadExpired(this, "Ask for a new upload slot and send the file again.", result.ExpiredAt!.Value);
            case ConfirmBusPhotoOutcome.NotUploaded:
                return ProblemResult.PhotoNotUploaded(this, "Send the file to the upload address, then confirm again.");
            case ConfirmBusPhotoOutcome.Invalid:
                string reason = JsonNamingPolicy.CamelCase.ConvertName(result.Fault!.Value.ToString());
                return ProblemResult.PhotoInvalid(this, "Upload a JPEG or PNG of the declared format, at most 3 MB.", reason);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
