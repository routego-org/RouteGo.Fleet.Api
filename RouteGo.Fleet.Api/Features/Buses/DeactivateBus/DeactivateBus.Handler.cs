namespace RouteGo.Fleet.Api.Features.Buses.DeactivateBus;

public sealed class DeactivateBusHandler
{
    private const string AuditAction = "DeactivateBus";

    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // Ownership is part of the lookup: another operator's bus is not found, exactly like one that
    // does not exist. The row lock keeps a schedule from giving the bus new trips between the
    // in-use check below and the status change.
    private const string SelectStatusForUpdateSql = """
        SELECT status
        FROM buses.buses
        WHERE bus_id = @BusId AND operator_id = @OperatorId
        FOR UPDATE
        """;

    // A scheduled trip whose departure has passed is departed, so only later ones keep the bus in use.
    private const string SelectFutureTripsSql = """
        SELECT count(*)::int AS future_trip_count, min(departure_at) AS next_departure_at
        FROM trips.trips
        WHERE bus_id = @BusId AND status = 'Scheduled' AND departure_at > @Now
        """;

    private const string DeactivateBusSql = """
        UPDATE buses.buses
        SET status = @Inactive
        WHERE bus_id = @BusId
        """;

    private const string SelectBusSql = """
        SELECT bus_id, operator_id, registration_number, name, bus_type, is_ac, amenities, status, created_at, updated_at
        FROM buses.buses
        WHERE bus_id = @BusId
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

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;
    private readonly PhotoAddresses _photoAddresses;

    public DeactivateBusHandler(IWriterConnectionFactory writerFactory, IClock clock, PhotoAddresses photoAddresses)
    {
        _writerFactory = writerFactory;
        _clock = clock;
        _photoAddresses = photoAddresses;
    }

    /// <summary>
    /// Takes one of the operator's buses out of service, provided the operator has its Fleet profile
    /// and the bus has no future scheduled trip. An inactive bus is returned unchanged.
    /// </summary>
    public async Task<DeactivateBusResult> HandleAsync(Guid busId, string operatorId, CancellationToken cancellationToken)
    {
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new DeactivateBusResult(DeactivateBusOutcome.OperatorProfileNotReady);
        }

        LockedBus? locked = await writer.Connection.QuerySingleOrDefaultAsync<LockedBus>(new CommandDefinition(
            SelectStatusForUpdateSql, new { BusId = busId, OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (locked is null)
        {
            return new DeactivateBusResult(DeactivateBusOutcome.NotFound);
        }

        // Deactivating an inactive bus changes nothing, so only an active one is checked and updated.
        if (locked.Status == BusStatus.Active)
        {
            BusUse? use = await DeactivateUnlessInUseAsync(writer, busId, cancellationToken);
            if (use != null)
            {
                return new DeactivateBusResult(DeactivateBusOutcome.BusInUse, Use: use);
            }
        }

        DeactivateBusResponse response = await GetBusAsync(writer, busId, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new DeactivateBusResult(DeactivateBusOutcome.Deactivated, response);
    }

    /// <summary>Makes the bus inactive, or returns its future trips and leaves it active.</summary>
    private async Task<BusUse?> DeactivateUnlessInUseAsync(IWriterConnection writer, Guid busId, CancellationToken cancellationToken)
    {
        var lookup = new { BusId = busId, Now = _clock.UtcNow };
        FutureTripsRow trips = await writer.Connection.QuerySingleAsync<FutureTripsRow>(new CommandDefinition(
            SelectFutureTripsSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        if (trips.FutureTripCount > 0)
        {
            return new BusUse(trips.FutureTripCount, ToUtc(trips.NextDepartureAt!.Value));
        }

        var deactivation = new { BusId = busId, Inactive = nameof(BusStatus.Inactive) };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            DeactivateBusSql, deactivation, writer.Transaction, cancellationToken: cancellationToken));
        return null;
    }

    private async Task<DeactivateBusResponse> GetBusAsync(IWriterConnection writer, Guid busId, CancellationToken cancellationToken)
    {
        BusRow bus = await writer.Connection.QuerySingleAsync<BusRow>(new CommandDefinition(
            SelectBusSql, new { BusId = busId }, writer.Transaction, cancellationToken: cancellationToken));
        await using SqlMapper.GridReader results = await writer.Connection.QueryMultipleAsync(new CommandDefinition(
            SelectPartsSql, new { BusId = busId }, writer.Transaction, cancellationToken: cancellationToken));
        IEnumerable<string> seatClasses = await results.ReadAsync<string>();
        IEnumerable<DeckRow> decks = await results.ReadAsync<DeckRow>();
        IEnumerable<SeatRow> seats = await results.ReadAsync<SeatRow>();
        IEnumerable<PhotoRow> photos = await results.ReadAsync<PhotoRow>();
        var parts = new BusParts
        {
            SeatClasses = seatClasses.ToList(),
            Decks = decks.ToList(),
            Seats = seats.ToList(),
            Photos = photos.ToList(),
        };
        return ToResponse(bus, parts, _photoAddresses);
    }

    private static DeactivateBusResponse ToResponse(BusRow bus, BusParts parts, PhotoAddresses photoAddresses)
    {
        SeatLayout layout = ToLayout(parts.Decks, parts.Seats);
        var photos = new List<BusPhoto>();
        foreach (PhotoRow photo in parts.Photos)
        {
            photos.Add(ToPhoto(photo, photoAddresses));
        }

        return new DeactivateBusResponse
        {
            BusId = bus.BusId,
            OperatorId = bus.OperatorId,
            RegistrationNumber = bus.RegistrationNumber,
            Name = bus.Name,
            BusType = bus.BusType,
            IsAc = bus.IsAc,
            Amenities = ToAmenities(bus.Amenities),
            SeatClasses = parts.SeatClasses,
            Layout = layout,
            TotalSeats = layout.Seats.Count,
            Status = bus.Status,
            Photos = photos,
            CreatedAt = ToUtc(bus.CreatedAt),
            UpdatedAt = ToUtc(bus.UpdatedAt),
        };
    }

    // A bus stored without amenities has a null array, which the contract shows as an empty list.
    private static List<Amenity> ToAmenities(string[]? stored)
    {
        var amenities = new List<Amenity>();
        if (stored is null)
        {
            return amenities;
        }

        foreach (string amenity in stored)
        {
            amenities.Add(Enum.Parse<Amenity>(amenity));
        }

        return amenities;
    }

    private static SeatLayout ToLayout(List<DeckRow> deckRows, List<SeatRow> seatRows)
    {
        var decks = new List<SeatLayoutDeck>();
        foreach (DeckRow deck in deckRows)
        {
            decks.Add(ToDeck(deck));
        }

        var seats = new List<SeatLayoutSeat>();
        foreach (SeatRow seat in seatRows)
        {
            seats.Add(ToSeat(seat));
        }

        return new SeatLayout(decks, seats);
    }

    private static SeatLayoutDeck ToDeck(DeckRow deck)
    {
        return new SeatLayoutDeck
        {
            Deck = deck.Deck,
            Rows = deck.Rows,
            Columns = deck.Columns,
            AisleAfterColumn = deck.AisleAfterColumn,
        };
    }

    private static SeatLayoutSeat ToSeat(SeatRow seat)
    {
        return new SeatLayoutSeat
        {
            Label = seat.Label,
            Deck = seat.Deck,
            Row = seat.Row,
            Column = seat.SeatColumn,
            SeatClass = seat.SeatClass,
        };
    }

    private static BusPhoto ToPhoto(PhotoRow photo, PhotoAddresses photoAddresses)
    {
        DateTimeOffset? uploadedAt = null;
        if (photo.UploadedAt != null)
        {
            uploadedAt = ToUtc(photo.UploadedAt.Value);
        }

        return new BusPhoto
        {
            PhotoId = photo.PhotoId,
            Status = photo.Status,
            Format = photo.Format,
            SizeBytes = photo.SizeBytes,
            Position = photo.Position,
            Url = photoAddresses.GetAddress(photo.FileLocation),
            CreatedAt = ToUtc(photo.CreatedAt),
            UploadedAt = uploadedAt,
        };
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private sealed class LockedBus
    {
        public BusStatus Status { get; init; }
    }

    private sealed class FutureTripsRow
    {
        public int FutureTripCount { get; init; }

        public DateTime? NextDepartureAt { get; init; }
    }

    private sealed class BusParts
    {
        public required List<string> SeatClasses { get; init; }

        public required List<DeckRow> Decks { get; init; }

        public required List<SeatRow> Seats { get; init; }

        public required List<PhotoRow> Photos { get; init; }
    }

    private sealed class BusRow
    {
        public Guid BusId { get; init; }

        public string OperatorId { get; init; } = string.Empty;

        public string RegistrationNumber { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public BusType BusType { get; init; }

        public bool IsAc { get; init; }

        public string[]? Amenities { get; init; }

        public BusStatus Status { get; init; }

        public DateTime CreatedAt { get; init; }

        public DateTime UpdatedAt { get; init; }
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
