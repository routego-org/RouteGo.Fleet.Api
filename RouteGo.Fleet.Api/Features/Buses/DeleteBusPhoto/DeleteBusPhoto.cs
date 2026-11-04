namespace RouteGo.Fleet.Api.Features.Buses.DeleteBusPhoto;

public enum DeleteBusPhotoOutcome
{
    Deleted,
    OperatorProfileNotReady,
    NotFound,
}

public sealed class DeleteBusPhotoHandler
{
    private const string AuditAction = "DeleteBusPhoto";

    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Ownership is part of the lookup: a photo of another operator's bus is not found, exactly like
    // one that does not exist. The bus row is locked so the photo order is renumbered by one
    // request at a time.
    private const string SelectPhotoForUpdateSql = """
        SELECT p.photo_id, p.file_location, p.position::int AS position
        FROM buses.buses b
        JOIN buses.bus_photos p ON p.bus_id = b.bus_id
        WHERE b.bus_id = @BusId AND b.operator_id = @OperatorId AND p.photo_id = @PhotoId
        FOR UPDATE OF b, p
        """;

    private const string DeletePhotoSql = """
        DELETE FROM buses.bus_photos WHERE photo_id = @PhotoId
        """;

    // The photos after the deleted one move up a place, so the order has no gap.
    private const string CloseGapSql = """
        UPDATE buses.bus_photos
        SET position = position - 1
        WHERE bus_id = @BusId AND position > @Position
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IMediaStore _mediaStore;

    public DeleteBusPhotoHandler(IWriterConnectionFactory writerFactory, IMediaStore mediaStore)
    {
        _writerFactory = writerFactory;
        _mediaStore = mediaStore;
    }

    /// <summary>
    /// Removes a photo (pending or uploaded) of one of the operator's buses together with its stored
    /// file, provided the operator has its Fleet profile; the remaining photos close up in order.
    /// </summary>
    public async Task<DeleteBusPhotoOutcome> HandleAsync(Guid busId, Guid photoId, string operatorId, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return DeleteBusPhotoOutcome.OperatorProfileNotReady;
        }

        var lookup = new { BusId = busId, PhotoId = photoId, OperatorId = operatorId };
        PhotoRow? photo = await writer.Connection.QuerySingleOrDefaultAsync<PhotoRow>(new CommandDefinition(
            SelectPhotoForUpdateSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        if (photo is null)
        {
            return DeleteBusPhotoOutcome.NotFound;
        }

        await RemoveAsync(writer, busId, photo, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return DeleteBusPhotoOutcome.Deleted;
    }

    // The file goes first: if removing it fails, the photo is kept and the deletion can be retried,
    // rather than leaving a file no photo points at.
    private async Task RemoveAsync(IWriterConnection writer, Guid busId, PhotoRow photo, CancellationToken cancellationToken)
    {
        await _mediaStore.DeleteFileAsync(photo.FileLocation, cancellationToken);
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            DeletePhotoSql, new { photo.PhotoId }, writer.Transaction, cancellationToken: cancellationToken));
        if (photo.Position != null)
        {
            await writer.Connection.ExecuteAsync(new CommandDefinition(
                CloseGapSql, new { BusId = busId, photo.Position }, writer.Transaction, cancellationToken: cancellationToken));
        }
    }

    private sealed class PhotoRow
    {
        public Guid PhotoId { get; init; }

        public string FileLocation { get; init; } = string.Empty;

        // Only an uploaded photo has a place in the order; a pending slot has none.
        public int? Position { get; init; }
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Buses")]
[Route("buses")]
public sealed class DeleteBusPhotoController : ControllerBase
{
    private readonly DeleteBusPhotoHandler _handler;

    public DeleteBusPhotoController(IWriterConnectionFactory writerFactory, IMediaStore mediaStore)
    {
        _handler = new DeleteBusPhotoHandler(writerFactory, mediaStore);
    }

    /// <summary>Remove a photo or an unused upload slot (Operator only).</summary>
    /// <remarks>
    /// Removes the photo (pending or uploaded) and its stored file; the remaining photos close up in
    /// order. Requires role Operator and the operator's Fleet profile. A bus that belongs to another
    /// operator is reported as not found.
    /// </remarks>
    /// <param name="busId">The bus's identifier.</param>
    /// <param name="photoId">The photo's identifier.</param>
    /// <param name="cancellationToken">Cancels the deletion.</param>
    /// <response code="204">The photo was removed.</response>
    /// <response code="400">An identifier is not a UUID (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an operator (FORBIDDEN), or its profile is not ready yet (OPERATOR_PROFILE_NOT_READY).</response>
    /// <response code="404">No such photo on a bus of the caller's (NOT_FOUND).</response>
    [Authorize(Policy = Policies.Operator)]
    [HttpDelete("{busId}/photos/{photoId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteBusPhotoAsync(Guid busId, Guid photoId, CancellationToken cancellationToken)
    {
        string operatorId = User.FindFirst(TokenClaims.OperatorId)!.Value;
        DeleteBusPhotoOutcome outcome = await _handler.HandleAsync(busId, photoId, operatorId, cancellationToken);
        switch (outcome)
        {
            case DeleteBusPhotoOutcome.Deleted:
                return NoContent();
            case DeleteBusPhotoOutcome.OperatorProfileNotReady:
                return ProblemResult.OperatorProfileNotReady(this, "Fleet has not yet created your operator profile.");
            case DeleteBusPhotoOutcome.NotFound:
                return ProblemResult.NotFound(this, $"Your bus {busId} has no photo {photoId}.");
            default:
                throw new InvalidOperationException($"Unhandled outcome '{outcome}'.");
        }
    }
}
