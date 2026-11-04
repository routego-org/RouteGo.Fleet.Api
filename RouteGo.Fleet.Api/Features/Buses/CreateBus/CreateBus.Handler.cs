namespace RouteGo.Fleet.Api.Features.Buses.CreateBus;

public sealed class CreateBusHandler
{
    private const string AuditAction = "CreateBus";
    private const string UniqueViolation = "23505";

    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    private const string RegistrationTakenSql = """
        SELECT EXISTS (SELECT 1 FROM buses.buses WHERE registration_number = @RegistrationNumber)
        """;

    private const string InsertBusSql = """
        INSERT INTO buses.buses (operator_id, registration_number, name, bus_type, is_ac, amenities, status)
        VALUES (@OperatorId, @RegistrationNumber, @Name, @BusType, @IsAc, @Amenities, @Status)
        RETURNING bus_id, created_at, updated_at
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

    public CreateBusHandler(IWriterConnectionFactory writerFactory)
    {
        _writerFactory = writerFactory;
    }

    /// <summary>
    /// Creates the active bus with its decks, seat classes and seats in one transaction, provided the
    /// operator has its Fleet profile and the registration number is not already in RouteGo.
    /// </summary>
    public async Task<CreateBusResult> HandleAsync(CreateBusRequest request, string operatorId, CancellationToken cancellationToken)
    {
        NewBus bus = Normalize(request, operatorId);

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await ExistsAsync(writer, ProfileExistsSql, new { bus.OperatorId }, cancellationToken);
        if (!hasProfile)
        {
            return new CreateBusResult(CreateBusOutcome.OperatorProfileNotReady);
        }

        bool isTaken = await ExistsAsync(writer, RegistrationTakenSql, new { bus.RegistrationNumber }, cancellationToken);
        if (isTaken)
        {
            return new CreateBusResult(CreateBusOutcome.RegistrationAlreadyExists);
        }

        InsertedBus? inserted = await TryInsertBusAsync(writer, bus, cancellationToken);
        if (inserted is null)
        {
            return new CreateBusResult(CreateBusOutcome.RegistrationAlreadyExists);
        }

        await InsertLayoutAsync(writer, inserted.BusId, bus, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new CreateBusResult(CreateBusOutcome.Created, ToResponse(bus, inserted));
    }

    // Registration numbers are kept normalized and class names trimmed; each seat names its class
    // exactly as the bus spells it, so the seat-to-class reference is a plain equality.
    private static NewBus Normalize(CreateBusRequest request, string operatorId)
    {
        var seatClasses = request.SeatClasses!.Select(seatClass => seatClass.Trim()).ToList();
        List<SeatLayoutDeck> decks = ToDecks(request.Layout!.Decks!);
        List<SeatLayoutSeat> seats = ToSeats(request.Layout.Seats!, seatClasses);
        return new NewBus
        {
            OperatorId = operatorId,
            RegistrationNumber = BusRules.NormalizeRegistrationNumber(request.RegistrationNumber!),
            Name = request.Name!,
            BusType = request.BusType!.Value,
            IsAc = request.IsAc!.Value,
            Amenities = request.Amenities!,
            SeatClasses = seatClasses,
            Layout = new SeatLayout(decks, seats),
        };
    }

    private static List<SeatLayoutDeck> ToDecks(IReadOnlyList<SeatLayoutDeckInput> inputs)
    {
        var decks = new List<SeatLayoutDeck>();
        foreach (SeatLayoutDeckInput deck in inputs)
        {
            decks.Add(new SeatLayoutDeck
            {
                Deck = deck.Deck!.Value,
                Rows = deck.Rows!.Value,
                Columns = deck.Columns!.Value,
                AisleAfterColumn = deck.AisleAfterColumn,
            });
        }

        return decks;
    }

    private static List<SeatLayoutSeat> ToSeats(IReadOnlyList<SeatLayoutSeatInput> inputs, List<string> seatClasses)
    {
        var seats = new List<SeatLayoutSeat>();
        foreach (SeatLayoutSeatInput seat in inputs)
        {
            seats.Add(new SeatLayoutSeat
            {
                Label = seat.Label!,
                Deck = seat.Deck!.Value,
                Row = seat.Row!.Value,
                Column = seat.Column!.Value,
                SeatClass = seatClasses.First(seatClass => BusRules.IsSameSeatClass(seatClass, seat.SeatClass!)),
            });
        }

        return seats;
    }

    private static async Task<bool> ExistsAsync(
        IWriterConnection writer, string sql, object parameters, CancellationToken cancellationToken)
    {
        return await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            sql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    // The check above answers the ordinary case; the unique index makes it atomic when two operators
    // register the same number at once.
    private static async Task<InsertedBus?> TryInsertBusAsync(
        IWriterConnection writer, NewBus bus, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            bus.OperatorId,
            bus.RegistrationNumber,
            bus.Name,
            BusType = bus.BusType.ToString(),
            bus.IsAc,
            Amenities = bus.Amenities.Select(amenity => amenity.ToString()).ToArray(),
            Status = nameof(BusStatus.Active),
        };
        try
        {
            return await writer.Connection.QuerySingleAsync<InsertedBus>(new CommandDefinition(
                InsertBusSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        }
        catch (DbException exception) when (exception.SqlState == UniqueViolation)
        {
            return null;
        }
    }

    // Decks and classes first: every seat references both.
    private static async Task InsertLayoutAsync(
        IWriterConnection writer, Guid busId, NewBus bus, CancellationToken cancellationToken)
    {
        IReadOnlyList<SeatLayoutDeck> decks = bus.Layout.Decks;
        var deckColumns = new
        {
            BusId = busId,
            Decks = decks.Select(deck => deck.Deck.ToString()).ToArray(),
            Rows = decks.Select(deck => deck.Rows).ToArray(),
            Columns = decks.Select(deck => deck.Columns).ToArray(),
            Aisles = decks.Select(deck => deck.AisleAfterColumn).ToArray(),
        };
        await ExecuteAsync(writer, InsertDecksSql, deckColumns, cancellationToken);
        await ExecuteAsync(writer, InsertSeatClassesSql, new { BusId = busId, Names = bus.SeatClasses.ToArray() }, cancellationToken);

        IReadOnlyList<SeatLayoutSeat> seats = bus.Layout.Seats;
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

    private static async Task ExecuteAsync(
        IWriterConnection writer, string sql, object parameters, CancellationToken cancellationToken)
    {
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            sql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static CreateBusResponse ToResponse(NewBus bus, InsertedBus inserted)
    {
        return new CreateBusResponse
        {
            BusId = inserted.BusId,
            OperatorId = bus.OperatorId,
            RegistrationNumber = bus.RegistrationNumber,
            Name = bus.Name,
            BusType = bus.BusType,
            IsAc = bus.IsAc,
            Amenities = bus.Amenities,
            SeatClasses = bus.SeatClasses,
            Layout = bus.Layout,
            TotalSeats = bus.Layout.Seats.Count,
            Status = BusStatus.Active,
            Photos = Array.Empty<BusPhoto>(),
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(inserted.CreatedAt, DateTimeKind.Utc)),
            UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(inserted.UpdatedAt, DateTimeKind.Utc)),
        };
    }

    private sealed record InsertedBus(Guid BusId, DateTime CreatedAt, DateTime UpdatedAt);

    private sealed class NewBus
    {
        public required string OperatorId { get; init; }

        public required string RegistrationNumber { get; init; }

        public required string Name { get; init; }

        public required BusType BusType { get; init; }

        public required bool IsAc { get; init; }

        public required IReadOnlyList<Amenity> Amenities { get; init; }

        public required IReadOnlyList<string> SeatClasses { get; init; }

        public required SeatLayout Layout { get; init; }
    }
}
