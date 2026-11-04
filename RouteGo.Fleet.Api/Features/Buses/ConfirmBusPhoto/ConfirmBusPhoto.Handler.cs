namespace RouteGo.Fleet.Api.Features.Buses.ConfirmBusPhoto;

public sealed class ConfirmBusPhotoHandler
{
    private const string AuditAction = "ConfirmBusPhoto";

    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Ownership is part of the lookup: a photo of another operator's bus is not found, exactly like
    // one that does not exist. The bus row is locked first, so two confirmations on one bus take
    // turns and never take the same position.
    private const string SelectPhotoForUpdateSql = """
        SELECT p.photo_id, p.bus_id, p.file_location, p.format, p.size_bytes::bigint AS size_bytes, p.status,
               p.position::int AS position, p.slot_expires_at, p.created_at, p.uploaded_at
        FROM buses.buses b
        JOIN buses.bus_photos p ON p.bus_id = b.bus_id
        WHERE b.bus_id = @BusId AND b.operator_id = @OperatorId AND p.photo_id = @PhotoId
        FOR UPDATE OF b, p
        """;

    private const string DiscardPhotoSql = """
        DELETE FROM buses.bus_photos WHERE photo_id = @PhotoId
        """;

    // A confirmed photo goes last in the bus's photo order.
    private const string ConfirmPhotoSql = """
        UPDATE buses.bus_photos
        SET status = @Uploaded, size_bytes = @SizeBytes, uploaded_at = @UploadedAt,
            position = (SELECT coalesce(max(position), 0) + 1 FROM buses.bus_photos WHERE bus_id = @BusId AND status = @Uploaded)
        WHERE photo_id = @PhotoId
        RETURNING position::int AS position, size_bytes::bigint AS size_bytes, uploaded_at
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;
    private readonly IMediaStore _mediaStore;
    private readonly PhotoAddresses _photoAddresses;

    public ConfirmBusPhotoHandler(
        IWriterConnectionFactory writerFactory, IClock clock, IMediaStore mediaStore, PhotoAddresses photoAddresses)
    {
        _writerFactory = writerFactory;
        _clock = clock;
        _mediaStore = mediaStore;
        _photoAddresses = photoAddresses;
    }

    /// <summary>
    /// Checks the file stored for a pending photo of one of the operator's buses and, when it is
    /// good, makes it the bus's last photo. An expired slot, or a file that breaks the photo rules,
    /// is discarded together with the file. An uploaded photo is returned unchanged.
    /// </summary>
    public async Task<ConfirmBusPhotoResult> HandleAsync(
        Guid busId, Guid photoId, string operatorId, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new ConfirmBusPhotoResult { Outcome = ConfirmBusPhotoOutcome.OperatorProfileNotReady };
        }

        var lookup = new { BusId = busId, PhotoId = photoId, OperatorId = operatorId };
        PhotoRow? photo = await writer.Connection.QuerySingleOrDefaultAsync<PhotoRow>(new CommandDefinition(
            SelectPhotoForUpdateSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        if (photo is null)
        {
            return new ConfirmBusPhotoResult { Outcome = ConfirmBusPhotoOutcome.NotFound };
        }

        if (photo.Status == BusPhotoStatus.Uploaded)
        {
            var unchanged = new UploadedRow { Position = photo.Position!.Value, SizeBytes = photo.SizeBytes, UploadedAt = photo.UploadedAt!.Value };
            return Confirmed(photo, unchanged, _photoAddresses);
        }

        return await ConfirmPendingAsync(writer, photo, cancellationToken);
    }

    private async Task<ConfirmBusPhotoResult> ConfirmPendingAsync(
        IWriterConnection writer, PhotoRow photo, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateTimeOffset expiresAt = ToUtc(photo.SlotExpiresAt);
        if (expiresAt <= now)
        {
            await DiscardAsync(writer, photo, cancellationToken);
            return new ConfirmBusPhotoResult { Outcome = ConfirmBusPhotoOutcome.UploadExpired, ExpiredAt = expiresAt };
        }

        StoredFile? file = await _mediaStore.GetFileAsync(photo.FileLocation, BusPhotoRules.SignatureLength, cancellationToken);
        if (file is null)
        {
            return new ConfirmBusPhotoResult { Outcome = ConfirmBusPhotoOutcome.NotUploaded };
        }

        PhotoFileFault? fault = BusPhotoRules.FindFault(photo.Format, file.SizeBytes, file.Header);
        if (fault != null)
        {
            await DiscardAsync(writer, photo, cancellationToken);
            return new ConfirmBusPhotoResult { Outcome = ConfirmBusPhotoOutcome.Invalid, Fault = fault };
        }

        var confirmation = new
        {
            photo.BusId,
            photo.PhotoId,
            file.SizeBytes,
            UploadedAt = now,
            Uploaded = nameof(BusPhotoStatus.Uploaded),
        };
        UploadedRow uploaded = await writer.Connection.QuerySingleAsync<UploadedRow>(new CommandDefinition(
            ConfirmPhotoSql, confirmation, writer.Transaction, cancellationToken: cancellationToken));
        await writer.CommitAsync(cancellationToken);
        return Confirmed(photo, uploaded, _photoAddresses);
    }

    // The file goes first: if removing it fails, the slot is kept and the confirmation can be
    // retried, rather than leaving a file no photo points at.
    private async Task DiscardAsync(IWriterConnection writer, PhotoRow photo, CancellationToken cancellationToken)
    {
        await _mediaStore.DeleteFileAsync(photo.FileLocation, cancellationToken);
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            DiscardPhotoSql, new { photo.PhotoId }, writer.Transaction, cancellationToken: cancellationToken));
        await writer.CommitAsync(cancellationToken);
    }

    private static ConfirmBusPhotoResult Confirmed(PhotoRow photo, UploadedRow uploaded, PhotoAddresses photoAddresses)
    {
        var response = new ConfirmBusPhotoResponse
        {
            PhotoId = photo.PhotoId,
            Status = BusPhotoStatus.Uploaded,
            Format = photo.Format,
            SizeBytes = uploaded.SizeBytes,
            Position = uploaded.Position,
            Url = photoAddresses.GetAddress(photo.FileLocation),
            CreatedAt = ToUtc(photo.CreatedAt),
            UploadedAt = ToUtc(uploaded.UploadedAt),
        };
        return new ConfirmBusPhotoResult { Outcome = ConfirmBusPhotoOutcome.Confirmed, Response = response };
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private sealed class UploadedRow
    {
        public int Position { get; init; }

        public long SizeBytes { get; init; }

        public DateTime UploadedAt { get; init; }
    }

    private sealed class PhotoRow
    {
        public Guid PhotoId { get; init; }

        public Guid BusId { get; init; }

        public string FileLocation { get; init; } = string.Empty;

        public PhotoFormat Format { get; init; }

        public long SizeBytes { get; init; }

        public BusPhotoStatus Status { get; init; }

        public int? Position { get; init; }

        public DateTime SlotExpiresAt { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime? UploadedAt { get; init; }
    }
}
