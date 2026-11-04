namespace RouteGo.Fleet.Api.Features.Buses.ReplaceBus;

public sealed partial class ReplaceBusHandler
{
    private const string AuditAction = "ReplaceBus";
    private const string UniqueViolation = "23505";

    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    private const string RegistrationTakenSql = """
        SELECT EXISTS (SELECT 1 FROM buses.buses WHERE registration_number = @RegistrationNumber AND bus_id <> @BusId)
        """;

    // A scheduled trip whose departure has passed is departed, so only later ones keep the bus in use.
    private const string SelectFutureTripsSql = """
        SELECT count(*)::int AS future_trip_count, min(departure_at) AS next_departure_at
        FROM trips.trips
        WHERE bus_id = @BusId AND status = 'Scheduled' AND departure_at > @Now
        """;

    private const string UpdateBusSql = """
        UPDATE buses.buses
        SET registration_number = @RegistrationNumber, name = @Name, bus_type = @BusType, is_ac = @IsAc, amenities = @Amenities
        WHERE bus_id = @BusId
        RETURNING updated_at
        """;

    // Seats first: each references its deck and its class.
    private const string DeleteLayoutSql = """
        DELETE FROM buses.bus_seats WHERE bus_id = @BusId;
        DELETE FROM buses.bus_decks WHERE bus_id = @BusId;
        DELETE FROM buses.seat_classes WHERE bus_id = @BusId;
        """;

    private const string InsertDecksSql = """
        INSERT INTO buses.bus_decks (bus_id, deck, rows, columns, aisle_after_column)
        SELECT @BusId, d.deck, d.rows, d.columns, d.aisle_after_column
        FROM unnest(@Decks, @Rows, @Columns, @Aisles) AS d(deck, rows, columns, aisle_after_column)
        """;

    // The order the operator listed the classes in becomes their display order.
    private const string InsertSeatClassesSql = """
        INSERT INTO buses.seat_classes (bus_id, name, display_order)
        SELECT @BusId, c.name, c.display_order
        FROM unnest(@Names) WITH ORDINALITY AS c(name, display_order)
        """;

    private const string InsertSeatsSql = """
        INSERT INTO buses.bus_seats (bus_id, label, deck, row, seat_column, seat_class)
        SELECT @BusId, s.label, s.deck, s.row, s.seat_column, s.seat_class
        FROM unnest(@Labels, @Decks, @Rows, @Columns, @SeatClasses) AS s(label, deck, row, seat_column, seat_class)
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly IClock _clock;
    private readonly PhotoAddresses _photoAddresses;

    public ReplaceBusHandler(IWriterConnectionFactory writerFactory, IClock clock, PhotoAddresses photoAddresses)
    {
        _writerFactory = writerFactory;
        _clock = clock;
        _photoAddresses = photoAddresses;
    }

    /// <summary>
    /// Replaces every detail of one of the operator's buses, provided the operator has its Fleet
    /// profile, the registration number is not another bus's, and the bus type, seat classes and
    /// layout stay as they are while the bus has future scheduled trips.
    /// </summary>
    public async Task<ReplaceBusResult> HandleAsync(
        Guid busId, ReplaceBusRequest request, string operatorId, CancellationToken cancellationToken)
    {
        BusDetails replacement = Normalize(request);

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await ExistsAsync(writer, ProfileExistsSql, new { OperatorId = operatorId }, cancellationToken);
        if (!hasProfile)
        {
            return new ReplaceBusResult(ReplaceBusOutcome.OperatorProfileNotReady);
        }

        StoredBus? stored = await GetStoredBusAsync(writer, busId, operatorId, cancellationToken);
        if (stored is null)
        {
            return new ReplaceBusResult(ReplaceBusOutcome.NotFound);
        }

        var registration = new { replacement.RegistrationNumber, BusId = busId };
        bool isTaken = await ExistsAsync(writer, RegistrationTakenSql, registration, cancellationToken);
        if (isTaken)
        {
            return new ReplaceBusResult(ReplaceBusOutcome.RegistrationAlreadyExists);
        }

        return await ReplaceAsync(writer, stored, replacement, cancellationToken);
    }

    private async Task<ReplaceBusResult> ReplaceAsync(
        IWriterConnection writer, StoredBus stored, BusDetails replacement, CancellationToken cancellationToken)
    {
        bool isLayoutKept = LockedDetails.IsSameLayout(
            stored.SeatClasses, stored.Layout, replacement.SeatClasses, replacement.Layout);
        bool areLockedDetailsKept = isLayoutKept && stored.BusType == replacement.BusType;
        if (!areLockedDetailsKept)
        {
            BusUse? use = await GetFutureUseAsync(writer, stored.BusId, cancellationToken);
            if (use != null)
            {
                return new ReplaceBusResult(ReplaceBusOutcome.BusInUse, Use: use);
            }
        }

        DateTime? updatedAt = await TryUpdateBusAsync(writer, stored.BusId, replacement, cancellationToken);
        if (updatedAt is null)
        {
            return new ReplaceBusResult(ReplaceBusOutcome.RegistrationAlreadyExists);
        }

        BusDetails replaced = replacement;
        if (isLayoutKept)
        {
            replaced = WithLayoutOf(replacement, stored);
        }
        else
        {
            await ReplaceLayoutAsync(writer, stored.BusId, replacement, cancellationToken);
        }

        await writer.CommitAsync(cancellationToken);
        return new ReplaceBusResult(ReplaceBusOutcome.Replaced, ToResponse(stored, replaced, updatedAt.Value));
    }

    // Registration numbers are kept normalized and class names trimmed; each seat names its class
    // exactly as the bus spells it, so the seat-to-class reference is a plain equality.
    private static BusDetails Normalize(ReplaceBusRequest request)
    {
        var seatClasses = request.SeatClasses!.Select(seatClass => seatClass.Trim()).ToList();
        var decks = new List<SeatLayoutDeck>();
        foreach (SeatLayoutDeckInput deck in request.Layout!.Decks!)
        {
            decks.Add(new SeatLayoutDeck { Deck = deck.Deck!.Value, Rows = deck.Rows!.Value, Columns = deck.Columns!.Value, AisleAfterColumn = deck.AisleAfterColumn });
        }

        var seats = new List<SeatLayoutSeat>();
        foreach (SeatLayoutSeatInput seat in request.Layout.Seats!)
        {
            string seatClass = seatClasses.First(busClass => BusRules.IsSameSeatClass(busClass, seat.SeatClass!));
            seats.Add(new SeatLayoutSeat { Label = seat.Label!, Deck = seat.Deck!.Value, Row = seat.Row!.Value, Column = seat.Column!.Value, SeatClass = seatClass });
        }

        return new BusDetails
        {
            RegistrationNumber = BusRules.NormalizeRegistrationNumber(request.RegistrationNumber!),
            Name = request.Name!,
            BusType = request.BusType!.Value,
            IsAc = request.IsAc!.Value,
            Amenities = request.Amenities!,
            SeatClasses = seatClasses,
            Layout = new SeatLayout(decks, seats),
        };
    }

    // An unchanged layout keeps the stored spelling and order, so trips already generated keep
    // matching the bus's class names exactly.
    private static BusDetails WithLayoutOf(BusDetails replacement, StoredBus stored)
    {
        return new BusDetails
        {
            RegistrationNumber = replacement.RegistrationNumber,
            Name = replacement.Name,
            BusType = replacement.BusType,
            IsAc = replacement.IsAc,
            Amenities = replacement.Amenities,
            SeatClasses = stored.SeatClasses,
            Layout = stored.Layout,
        };
    }

    private async Task<BusUse?> GetFutureUseAsync(IWriterConnection writer, Guid busId, CancellationToken cancellationToken)
    {
        var lookup = new { BusId = busId, Now = _clock.UtcNow };
        FutureTripsRow trips = await writer.Connection.QuerySingleAsync<FutureTripsRow>(new CommandDefinition(
            SelectFutureTripsSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        if (trips.FutureTripCount == 0)
        {
            return null;
        }

        return new BusUse(trips.FutureTripCount, ToUtc(trips.NextDepartureAt!.Value));
    }

    // The check before answers the ordinary case; the unique index makes it atomic when another
    // bus takes the same number at once.
    private static async Task<DateTime?> TryUpdateBusAsync(
        IWriterConnection writer, Guid busId, BusDetails replacement, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            BusId = busId,
            replacement.RegistrationNumber,
            replacement.Name,
            BusType = replacement.BusType.ToString(),
            replacement.IsAc,
            Amenities = replacement.Amenities.Select(amenity => amenity.ToString()).ToArray(),
        };
        try
        {
            return await writer.Connection.QuerySingleAsync<DateTime>(new CommandDefinition(
                UpdateBusSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        }
        catch (DbException exception) when (exception.SqlState == UniqueViolation)
        {
            return null;
        }
    }

    // Decks and classes first: every seat references both.
    private static async Task ReplaceLayoutAsync(
        IWriterConnection writer, Guid busId, BusDetails replacement, CancellationToken cancellationToken)
    {
        await ExecuteAsync(writer, DeleteLayoutSql, new { BusId = busId }, cancellationToken);

        IReadOnlyList<SeatLayoutDeck> decks = replacement.Layout.Decks;
        var deckColumns = new
        {
            BusId = busId,
            Decks = decks.Select(deck => deck.Deck.ToString()).ToArray(),
            Rows = decks.Select(deck => deck.Rows).ToArray(),
            Columns = decks.Select(deck => deck.Columns).ToArray(),
            Aisles = decks.Select(deck => deck.AisleAfterColumn).ToArray(),
        };
        await ExecuteAsync(writer, InsertDecksSql, deckColumns, cancellationToken);
        await ExecuteAsync(writer, InsertSeatClassesSql, new { BusId = busId, Names = replacement.SeatClasses.ToArray() }, cancellationToken);

        IReadOnlyList<SeatLayoutSeat> seats = replacement.Layout.Seats;
        var seatColumns = new
        {
            BusId = busId,
            Labels = seats.Select(seat => seat.Label).ToArray(),
            Decks = seats.Select(seat => seat.Deck.ToString()).ToArray(),
            Rows = seats.Select(seat => seat.Row).ToArray(),
            Columns = seats.Select(seat => seat.Column).ToArray(),
            SeatClasses = seats.Select(seat => seat.SeatClass).ToArray(),
        };
        await ExecuteAsync(writer, InsertSeatsSql, seatColumns, cancellationToken);
    }

    private static async Task<bool> ExistsAsync(
        IWriterConnection writer, string sql, object parameters, CancellationToken cancellationToken)
    {
        return await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            sql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static async Task ExecuteAsync(
        IWriterConnection writer, string sql, object parameters, CancellationToken cancellationToken)
    {
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            sql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static ReplaceBusResponse ToResponse(StoredBus stored, BusDetails replaced, DateTime updatedAt)
    {
        return new ReplaceBusResponse
        {
            BusId = stored.BusId,
            OperatorId = stored.OperatorId,
            RegistrationNumber = replaced.RegistrationNumber,
            Name = replaced.Name,
            BusType = replaced.BusType,
            IsAc = replaced.IsAc,
            Amenities = replaced.Amenities,
            SeatClasses = replaced.SeatClasses,
            Layout = replaced.Layout,
            TotalSeats = replaced.Layout.Seats.Count,
            Status = stored.Status,
            Photos = stored.Photos,
            CreatedAt = stored.CreatedAt,
            UpdatedAt = ToUtc(updatedAt),
        };
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    /// <summary>Every detail the operator gives when replacing a bus, normalized.</summary>
    private sealed class BusDetails
    {
        public required string RegistrationNumber { get; init; }

        public required string Name { get; init; }

        public required BusType BusType { get; init; }

        public required bool IsAc { get; init; }

        public required IReadOnlyList<Amenity> Amenities { get; init; }

        public required IReadOnlyList<string> SeatClasses { get; init; }

        public required SeatLayout Layout { get; init; }
    }

    private sealed class FutureTripsRow
    {
        public int FutureTripCount { get; init; }

        public DateTime? NextDepartureAt { get; init; }
    }
}
