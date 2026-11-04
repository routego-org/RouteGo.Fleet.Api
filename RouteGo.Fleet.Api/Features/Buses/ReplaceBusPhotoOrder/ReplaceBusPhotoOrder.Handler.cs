namespace RouteGo.Fleet.Api.Features.Buses.ReplaceBusPhotoOrder;

public sealed class ReplaceBusPhotoOrderHandler
{
    private const string AuditAction = "ReplaceBusPhotoOrder";

    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Ownership is part of the lookup: another operator's bus is not found, exactly like one that
    // does not exist. The row lock keeps a confirmation or deletion from changing the set of
    // uploaded photos while they are reordered.
    private const string SelectBusForUpdateSql = """
        SELECT bus_id
        FROM buses.buses
        WHERE bus_id = @BusId AND operator_id = @OperatorId
        FOR UPDATE
        """;

    private const string SelectUploadedPhotoIdsSql = """
        SELECT photo_id
        FROM buses.bus_photos
        WHERE bus_id = @BusId AND status = 'Uploaded'
        """;

    // Each photo takes its place in the list; positions are unique only at commit, so swapping
    // two photos in one statement is allowed.
    private const string ReorderSql = """
        UPDATE buses.bus_photos p
        SET position = o.position
        FROM unnest(@PhotoIds) WITH ORDINALITY AS o(photo_id, position)
        WHERE p.photo_id = o.photo_id AND p.bus_id = @BusId
        """;

    private const string SelectPhotosSql = """
        SELECT photo_id, status, format, size_bytes::bigint AS size_bytes, position::int AS position, file_location,
               created_at, uploaded_at
        FROM buses.bus_photos
        WHERE bus_id = @BusId AND status = 'Uploaded'
        ORDER BY position
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly PhotoAddresses _photoAddresses;

    public ReplaceBusPhotoOrderHandler(IWriterConnectionFactory writerFactory, PhotoAddresses photoAddresses)
    {
        _writerFactory = writerFactory;
        _photoAddresses = photoAddresses;
    }

    /// <summary>
    /// Puts the uploaded photos of one of the operator's buses in the given order, provided the
    /// operator has its Fleet profile and the list names every uploaded photo exactly once.
    /// </summary>
    public async Task<ReplaceBusPhotoOrderResult> HandleAsync(
        Guid busId, IReadOnlyList<Guid> photoIds, string operatorId, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new ReplaceBusPhotoOrderResult(ReplaceBusPhotoOrderOutcome.OperatorProfileNotReady);
        }

        Guid? lockedBusId = await writer.Connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            SelectBusForUpdateSql, new { BusId = busId, OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (lockedBusId is null)
        {
            return new ReplaceBusPhotoOrderResult(ReplaceBusPhotoOrderOutcome.NotFound);
        }

        IEnumerable<Guid> uploaded = await writer.Connection.QueryAsync<Guid>(new CommandDefinition(
            SelectUploadedPhotoIdsSql, new { BusId = busId }, writer.Transaction, cancellationToken: cancellationToken));
        if (!IsEveryUploadedPhotoOnce(uploaded.ToList(), photoIds))
        {
            return new ReplaceBusPhotoOrderResult(ReplaceBusPhotoOrderOutcome.NotTheUploadedPhotos);
        }

        ReplaceBusPhotoOrderResponse response = await ReorderAsync(writer, busId, photoIds, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new ReplaceBusPhotoOrderResult(ReplaceBusPhotoOrderOutcome.Replaced, response);
    }

    // The validator has already refused repeats, so equal counts plus every listed photo being an
    // uploaded one means the list is exactly the uploaded photos.
    private static bool IsEveryUploadedPhotoOnce(List<Guid> uploaded, IReadOnlyList<Guid> photoIds)
    {
        return uploaded.Count == photoIds.Count && photoIds.All(uploaded.Contains);
    }

    private async Task<ReplaceBusPhotoOrderResponse> ReorderAsync(
        IWriterConnection writer, Guid busId, IReadOnlyList<Guid> photoIds, CancellationToken cancellationToken)
    {
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            ReorderSql, new { BusId = busId, PhotoIds = photoIds.ToArray() }, writer.Transaction, cancellationToken: cancellationToken));
        IEnumerable<PhotoRow> rows = await writer.Connection.QueryAsync<PhotoRow>(new CommandDefinition(
            SelectPhotosSql, new { BusId = busId }, writer.Transaction, cancellationToken: cancellationToken));
        return ToResponse(rows, _photoAddresses);
    }

    private static ReplaceBusPhotoOrderResponse ToResponse(IEnumerable<PhotoRow> rows, PhotoAddresses photoAddresses)
    {
        var photos = new List<BusPhoto>();
        foreach (PhotoRow photo in rows)
        {
            photos.Add(new BusPhoto
            {
                PhotoId = photo.PhotoId,
                Status = photo.Status,
                Format = photo.Format,
                SizeBytes = photo.SizeBytes,
                Position = photo.Position,
                Url = photoAddresses.GetAddress(photo.FileLocation),
                CreatedAt = ToUtc(photo.CreatedAt),
                UploadedAt = ToUtc(photo.UploadedAt),
            });
        }

        return new ReplaceBusPhotoOrderResponse(photos);
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private sealed class PhotoRow
    {
        public Guid PhotoId { get; init; }

        public BusPhotoStatus Status { get; init; }

        public PhotoFormat Format { get; init; }

        public long SizeBytes { get; init; }

        public int Position { get; init; }

        public string FileLocation { get; init; } = string.Empty;

        public DateTime CreatedAt { get; init; }

        // Every photo listed here is uploaded, so it always has an upload time.
        public DateTime UploadedAt { get; init; }
    }
}
