namespace RouteGo.Fleet.Api.Features.Buses.CreateBusPhoto;

/// <summary>The photo file the operator is about to upload.</summary>
public sealed class CreateBusPhotoRequest
{
    /// <summary>JPEG or PNG; the upload must declare the matching content type.</summary>
    public PhotoFormat? Format { get; init; }

    /// <summary>The file's size in bytes: 1 to 3,145,728 (3 MB).</summary>
    public long? SizeBytes { get; init; }
}

/// <summary>The HTTP method an upload uses.</summary>
public enum UploadMethod
{
    Put,
}

/// <summary>Where and how the browser sends the photo file, and until when.</summary>
public sealed class CreateBusPhotoResponse
{
    /// <summary>The new pending photo.</summary>
    public required Guid PhotoId { get; init; }

    /// <summary>Where the browser sends the file; valid for this one photo until <see cref="ExpiresAt"/>.</summary>
    public required Uri UploadUrl { get; init; }

    public required UploadMethod UploadMethod { get; init; }

    /// <summary>The content type the upload must declare: image/jpeg or image/png.</summary>
    public required string ContentType { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

public enum CreateBusPhotoOutcome
{
    Created,
    OperatorProfileNotReady,
    NotFound,
    PhotoLimitReached,
}

/// <summary>How the request ended, with the upload slot when one was issued.</summary>
/// <param name="Outcome">How the request ended.</param>
/// <param name="Response">The upload slot when issued.</param>
public sealed record CreateBusPhotoResult(CreateBusPhotoOutcome Outcome, CreateBusPhotoResponse? Response = null);

public sealed class CreateBusPhotoValidator : AbstractValidator<CreateBusPhotoRequest>
{
    public CreateBusPhotoValidator()
    {
        RuleFor(x => x.Format).NotNull().WithMessage("The format is required: jpeg or png.");
        RuleFor(x => x.SizeBytes).Must(BusPhotoRules.IsAcceptableSize)
            .WithMessage("The size must be 1 to 3,145,728 bytes (3 MB).");
    }
}

public sealed class CreateBusPhotoHandler
{
    private const string AuditAction = "CreateBusPhoto";

    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Ownership is part of the lookup: another operator's bus is not found, exactly like one that
    // does not exist. The row lock makes two slot requests for one bus take turns, so together they
    // cannot pass the photo limit.
    private const string SelectBusForUpdateSql = """
        SELECT bus_id
        FROM buses.buses
        WHERE bus_id = @BusId AND operator_id = @OperatorId
        FOR UPDATE
        """;

    // An expired slot can no longer receive a file, so it no longer counts toward the limit.
    private const string CountPhotosSql = """
        SELECT count(*)::int
        FROM buses.bus_photos
        WHERE bus_id = @BusId AND (status = 'Uploaded' OR slot_expires_at > @Now)
        """;

    private const string InsertPhotoSql = """
        INSERT INTO buses.bus_photos (photo_id, bus_id, file_location, format, size_bytes, status, slot_expires_at)
        VALUES (@PhotoId, @BusId, @FileLocation, @Format, @SizeBytes, @Status, @SlotExpiresAt)
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;
    private readonly IMediaStore _mediaStore;

    public CreateBusPhotoHandler(IWriterConnectionFactory writerFactory, IClock clock, IMediaStore mediaStore)
    {
        _writerFactory = writerFactory;
        _clock = clock;
        _mediaStore = mediaStore;
    }

    /// <summary>
    /// Creates a pending photo of one of the operator's buses and the upload slot for its file,
    /// provided the operator has its Fleet profile and the bus is below its photo limit.
    /// </summary>
    public async Task<CreateBusPhotoResult> HandleAsync(
        Guid busId, CreateBusPhotoRequest request, string operatorId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new CreateBusPhotoResult(CreateBusPhotoOutcome.OperatorProfileNotReady);
        }

        Guid? lockedBusId = await writer.Connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            SelectBusForUpdateSql, new { BusId = busId, OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (lockedBusId is null)
        {
            return new CreateBusPhotoResult(CreateBusPhotoOutcome.NotFound);
        }

        int photoCount = await writer.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            CountPhotosSql, new { BusId = busId, Now = now }, writer.Transaction, cancellationToken: cancellationToken));
        if (photoCount >= BusPhotoRules.MaxPhotosPerBus)
        {
            return new CreateBusPhotoResult(CreateBusPhotoOutcome.PhotoLimitReached);
        }

        CreateBusPhotoResponse slot = await CreateSlotAsync(writer, busId, request, now, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new CreateBusPhotoResult(CreateBusPhotoOutcome.Created, slot);
    }

    private async Task<CreateBusPhotoResponse> CreateSlotAsync(
        IWriterConnection writer, Guid busId, CreateBusPhotoRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var photoId = Guid.NewGuid();
        PhotoFormat format = request.Format!.Value;
        string fileLocation = BusPhotoRules.GetFileLocation(busId, photoId);
        DateTimeOffset expiresAt = now + BusPhotoRules.UploadSlotLifetime;
        var photo = new
        {
            PhotoId = photoId,
            BusId = busId,
            FileLocation = fileLocation,
            Format = format.ToString(),
            SizeBytes = request.SizeBytes!.Value,
            Status = nameof(BusPhotoStatus.Pending),
            SlotExpiresAt = expiresAt,
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertPhotoSql, photo, writer.Transaction, cancellationToken: cancellationToken));

        string contentType = BusPhotoRules.GetContentType(format);
        return new CreateBusPhotoResponse
        {
            PhotoId = photoId,
            UploadUrl = _mediaStore.CreateUploadAddress(fileLocation, contentType, expiresAt),
            UploadMethod = UploadMethod.Put,
            ContentType = contentType,
            ExpiresAt = expiresAt,
        };
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Buses")]
[Route("buses")]
public sealed class CreateBusPhotoController : ControllerBase
{
    private readonly CreateBusPhotoHandler _handler;
    private readonly CreateBusPhotoValidator _validator = new CreateBusPhotoValidator();

    public CreateBusPhotoController(IWriterConnectionFactory writerFactory, IClock clock, IMediaStore mediaStore)
    {
        _handler = new CreateBusPhotoHandler(writerFactory, clock, mediaStore);
    }

    /// <summary>Issue an upload slot for a new photo of a bus (Operator only).</summary>
    /// <remarks>
    /// Creates a pending photo of the bus and returns the address to which the browser sends the
    /// file, the content type the upload must declare, and when the slot expires (5 minutes). The
    /// file goes straight to the media store, never through Fleet. A bus has at most 5 photos,
    /// counting uploaded photos and unexpired slots. Requires role Operator and the operator's Fleet
    /// profile. A bus that belongs to another operator is reported as not found.
    /// </remarks>
    /// <param name="busId">The bus's identifier.</param>
    /// <param name="request">The format and size of the file to upload.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="201">The upload slot.</response>
    /// <response code="400">The format or size is invalid (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="404">No bus with this identifier belongs to the caller (NOT_FOUND).</response>
    /// <response code="422">The bus already has 5 photos or open upload slots (PHOTO_LIMIT_REACHED).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpPost("{busId}/photos")]
    [ProducesResponseType(typeof(CreateBusPhotoResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateBusPhotoAsync(Guid busId, CreateBusPhotoRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        CreateBusPhotoResult result = await _handler.HandleAsync(busId, request, operatorId, cancellationToken);
        switch (result.Outcome)
        {
            // The contract gives the slot no Location: a photo has no address of its own to read.
            case CreateBusPhotoOutcome.Created:
                return StatusCode(StatusCodes.Status201Created, result.Response);
            case CreateBusPhotoOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case CreateBusPhotoOutcome.NotFound:
                return ProblemResult.NotFound(this, $"You have no bus {busId}.");
            case CreateBusPhotoOutcome.PhotoLimitReached:
                return ProblemResult.PhotoLimitReached(this,
                    "Delete a photo, or wait for an unused upload slot to expire.", BusPhotoRules.MaxPhotosPerBus);
            default:
                throw new InvalidOperationException($"Unhandled outcome '{result.Outcome}'.");
        }
    }
}
