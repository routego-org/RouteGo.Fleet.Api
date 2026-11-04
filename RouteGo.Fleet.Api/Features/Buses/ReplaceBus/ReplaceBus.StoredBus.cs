namespace RouteGo.Fleet.Api.Features.Buses.ReplaceBus;

// Reading the bus as it is stored, locked for the replacement: what the locked details are
// compared against, and what the response keeps from it.
public sealed partial class ReplaceBusHandler
{
    // Ownership is part of the lookup: another operator's bus is not found, exactly like one that
    // does not exist. The row lock keeps a schedule from giving the bus new trips between the
    // in-use check and the layout change.
    private const string SelectBusForUpdateSql = """
        SELECT bus_id, operator_id, bus_type, status, created_at
        FROM buses.buses
        WHERE bus_id = @BusId AND operator_id = @OperatorId
        FOR UPDATE
        """;

    // Only uploaded photos belong to the bus's photo order; a pending upload slot is not shown.
    private const string SelectPartsSql = """
        SELECT name
        FROM buses.seat_classes
        WHERE bus_id = @BusId
        ORDER BY display_order;

        SELECT deck, rows::int AS rows, columns::int AS columns, aisle_after_column::int AS aisle_after_column
        FROM buses.bus_decks
        WHERE bus_id = @BusId
        ORDER BY deck;

        SELECT label, deck, row::int AS row, seat_column::int AS seat_column, seat_class
        FROM buses.bus_seats
        WHERE bus_id = @BusId
        ORDER BY deck, row, seat_column;

        SELECT photo_id, status, format, size_bytes::bigint AS size_bytes, position::int AS position, file_location,
               created_at, uploaded_at
        FROM buses.bus_photos
        WHERE bus_id = @BusId AND status = 'Uploaded'
        ORDER BY position;
        """;

    private async Task<StoredBus?> GetStoredBusAsync(
        IWriterConnection writer, Guid busId, string operatorId, CancellationToken cancellationToken)
    {
        BusRow? bus = await writer.Connection.QuerySingleOrDefaultAsync<BusRow>(new CommandDefinition(
            SelectBusForUpdateSql, new { BusId = busId, OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (bus is null)
        {
            return null;
        }

        await using SqlMapper.GridReader results = await writer.Connection.QueryMultipleAsync(new CommandDefinition(
            SelectPartsSql, new { BusId = busId }, writer.Transaction, cancellationToken: cancellationToken));
        IEnumerable<string> seatClasses = await results.ReadAsync<string>();
        IEnumerable<DeckRow> decks = await results.ReadAsync<DeckRow>();
        IEnumerable<SeatRow> seats = await results.ReadAsync<SeatRow>();
        IEnumerable<PhotoRow> photos = await results.ReadAsync<PhotoRow>();
        return ToStoredBus(bus, seatClasses.ToList(), ToLayout(decks, seats), ToPhotos(photos, _photoAddresses));
    }

    private static StoredBus ToStoredBus(BusRow bus, List<string> seatClasses, SeatLayout layout, List<BusPhoto> photos)
    {
        return new StoredBus
        {
            BusId = bus.BusId,
            OperatorId = bus.OperatorId,
            BusType = bus.BusType,
            Status = bus.Status,
            CreatedAt = ToUtc(bus.CreatedAt),
            SeatClasses = seatClasses,
            Layout = layout,
            Photos = photos,
        };
    }

    private static SeatLayout ToLayout(IEnumerable<DeckRow> deckRows, IEnumerable<SeatRow> seatRows)
    {
        var decks = new List<SeatLayoutDeck>();
        foreach (DeckRow deck in deckRows)
        {
            decks.Add(new SeatLayoutDeck { Deck = deck.Deck, Rows = deck.Rows, Columns = deck.Columns, AisleAfterColumn = deck.AisleAfterColumn });
        }

        var seats = new List<SeatLayoutSeat>();
        foreach (SeatRow seat in seatRows)
        {
            seats.Add(new SeatLayoutSeat { Label = seat.Label, Deck = seat.Deck, Row = seat.Row, Column = seat.SeatColumn, SeatClass = seat.SeatClass });
        }

        return new SeatLayout(decks, seats);
    }

    private static List<BusPhoto> ToPhotos(IEnumerable<PhotoRow> rows, PhotoAddresses photoAddresses)
    {
        var photos = new List<BusPhoto>();
        foreach (PhotoRow photo in rows)
        {
            DateTimeOffset? uploadedAt = null;
            if (photo.UploadedAt != null)
            {
                uploadedAt = ToUtc(photo.UploadedAt.Value);
            }

            photos.Add(new BusPhoto
            {
                PhotoId = photo.PhotoId,
                Status = photo.Status,
                Format = photo.Format,
                SizeBytes = photo.SizeBytes,
                Position = photo.Position,
                Url = photoAddresses.GetAddress(photo.FileLocation),
                CreatedAt = ToUtc(photo.CreatedAt),
                UploadedAt = uploadedAt,
            });
        }

        return photos;
    }

    private sealed class StoredBus
    {
        public required Guid BusId { get; init; }

        public required string OperatorId { get; init; }

        public required BusType BusType { get; init; }

        public required BusStatus Status { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required IReadOnlyList<string> SeatClasses { get; init; }

        public required SeatLayout Layout { get; init; }

        public required IReadOnlyList<BusPhoto> Photos { get; init; }
    }

    private sealed class BusRow
    {
        public Guid BusId { get; init; }

        public string OperatorId { get; init; } = string.Empty;

        public BusType BusType { get; init; }

        public BusStatus Status { get; init; }

        public DateTime CreatedAt { get; init; }
    }

    private sealed class DeckRow
    {
        public Deck Deck { get; init; }

        public int Rows { get; init; }

        public int Columns { get; init; }

        public int? AisleAfterColumn { get; init; }
    }

    // The table names the column seat_column because COLUMN is a reserved SQL word.
    private sealed class SeatRow
    {
        public string Label { get; init; } = string.Empty;

        public Deck Deck { get; init; }

        public int Row { get; init; }

        public int SeatColumn { get; init; }

        public string SeatClass { get; init; } = string.Empty;
    }

    private sealed class PhotoRow
    {
        public Guid PhotoId { get; init; }

        public BusPhotoStatus Status { get; init; }

        public PhotoFormat Format { get; init; }

        public long SizeBytes { get; init; }

        public int? Position { get; init; }

        public string FileLocation { get; init; } = string.Empty;

        public DateTime CreatedAt { get; init; }

        public DateTime? UploadedAt { get; init; }
    }
}
