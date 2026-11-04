namespace RouteGo.Fleet.Api.Features.Buses.ReplaceBus;

public sealed class ReplaceBusValidator : AbstractValidator<ReplaceBusRequest>
{
    public ReplaceBusValidator()
    {
        RuleFor(x => x.RegistrationNumber).Must(BusRules.IsAcceptableRegistrationNumber)
            .WithMessage("The registration number must be letters and digits, optionally with spaces and hyphens, and 6 to 15 characters once those are removed.");
        RuleFor(x => x.Name).Must(BusRules.IsAcceptableName)
            .WithMessage("The name must be 2 to 80 characters.");
        RuleFor(x => x.BusType).NotNull().WithMessage("The bus type is required.");
        RuleFor(x => x.IsAc).NotNull().WithMessage("Whether the bus is air-conditioned is required.");
        RuleFor(x => x.Amenities).NotNull().WithMessage("The amenities are required; send an empty list for none.");
        RuleFor(x => x.Amenities!).Must(HaveNoRepeats).When(x => x.Amenities != null)
            .WithMessage("Each amenity may be listed at most once.");
        RuleFor(x => x.SeatClasses).NotNull().WithMessage("The seat classes are required.");
        RuleFor(x => x.SeatClasses!).Must(HaveAcceptableCount).When(x => x.SeatClasses != null)
            .WithMessage("A bus has 1 to 5 seat classes.");
        RuleForEach(x => x.SeatClasses).Must(BusRules.IsAcceptableSeatClassName)
            .WithMessage("Each seat class name must be 2 to 30 characters.");
        RuleForEach(x => x.SeatClasses).Must(BeNamedOnce).When(x => x.SeatClasses != null)
            .WithMessage("Seat class names must be unique, ignoring case.");
        RuleForEach(x => x.SeatClasses).Must(HaveASeat).When(HasSeats)
            .WithMessage("Every seat class must have at least one seat.");
        RuleFor(x => x.Layout).NotNull().WithMessage("The seat layout is required.");
        RuleFor(x => x.Layout!).SetValidator(request => new SeatLayoutValidator(request.SeatClasses));
    }

    private static bool HaveNoRepeats(IReadOnlyList<Amenity> amenities)
    {
        return amenities.Distinct().Count() == amenities.Count;
    }

    private static bool HaveAcceptableCount(IReadOnlyList<string> seatClasses)
    {
        return seatClasses.Count >= 1 && seatClasses.Count <= BusRules.MaxSeatClasses;
    }

    private static bool BeNamedOnce(ReplaceBusRequest request, string? seatClass)
    {
        if (seatClass is null)
        {
            return true;
        }

        int namings = request.SeatClasses!.Count(other => other != null && BusRules.IsSameSeatClass(other, seatClass));
        return namings == 1;
    }

    private static bool HasSeats(ReplaceBusRequest request)
    {
        return request.Layout?.Seats != null;
    }

    private static bool HaveASeat(ReplaceBusRequest request, string? seatClass)
    {
        if (seatClass is null)
        {
            return true;
        }

        return request.Layout!.Seats!.Any(seat => seat.SeatClass != null && BusRules.IsSameSeatClass(seat.SeatClass, seatClass));
    }
}

public sealed class SeatLayoutValidator : AbstractValidator<SeatLayoutInput>
{
    public SeatLayoutValidator(IReadOnlyList<string>? seatClasses)
    {
        RuleFor(x => x.Decks).NotNull().WithMessage("The decks are required.");
        RuleFor(x => x.Decks!).Must(HaveAcceptableDecks).When(x => x.Decks != null)
            .WithMessage("A bus has one deck (lower) or two (lower and upper).");
        RuleForEach(x => x.Decks).SetValidator(new SeatLayoutDeckValidator());
        RuleFor(x => x.Seats).NotNull().WithMessage("The seats are required.");
        RuleFor(x => x.Seats!).Must(HaveAcceptableSeatCount).When(x => x.Seats != null)
            .WithMessage("A bus has 1 to 60 seats.");
        RuleForEach(x => x.Seats).SetValidator((layout, _) => new SeatLayoutSeatValidator(layout, seatClasses));
    }

    private static bool HaveAcceptableDecks(IReadOnlyList<SeatLayoutDeckInput> decks)
    {
        if (decks.Count < 1 || decks.Count > BusRules.MaxDecks)
        {
            return false;
        }

        var named = decks.Where(deck => deck.Deck != null).Select(deck => deck.Deck!.Value).ToList();
        bool isEachDeckNamedOnce = named.Distinct().Count() == named.Count;
        return isEachDeckNamedOnce && BusRules.HasLowerDeck(named);
    }

    private static bool HaveAcceptableSeatCount(IReadOnlyList<SeatLayoutSeatInput> seats)
    {
        return seats.Count >= 1 && seats.Count <= BusRules.MaxSeats;
    }
}

public sealed class SeatLayoutDeckValidator : AbstractValidator<SeatLayoutDeckInput>
{
    public SeatLayoutDeckValidator()
    {
        RuleFor(x => x.Deck).NotNull().WithMessage("The deck is required.");
        RuleFor(x => x.Rows).Must(rows => rows != null && BusRules.IsAcceptableRows(rows.Value))
            .WithMessage("A deck has 1 to 15 rows.");
        RuleFor(x => x.Columns).Must(columns => columns != null && BusRules.IsAcceptableColumns(columns.Value))
            .WithMessage("A deck has 1 to 6 columns.");
        RuleFor(x => x.AisleAfterColumn).Must((deck, aisle) => BusRules.IsAcceptableAisle(aisle, deck.Columns!.Value))
            .When(x => x.Columns != null)
            .WithMessage("The aisle must run after a column from 1 to one less than the deck's columns.");
    }
}

public sealed class SeatLayoutSeatValidator : AbstractValidator<SeatLayoutSeatInput>
{
    private readonly SeatLayoutInput _layout;
    private readonly IReadOnlyList<string>? _seatClasses;

    public SeatLayoutSeatValidator(SeatLayoutInput layout, IReadOnlyList<string>? seatClasses)
    {
        _layout = layout;
        _seatClasses = seatClasses;

        RuleFor(x => x.Label).Must(BusRules.IsAcceptableSeatLabel)
            .WithMessage("A seat label is 1 to 5 letters or digits.");
        RuleFor(x => x.Label).Must(BeLabelledOnce).When(x => x.Label != null)
            .WithMessage("Seat labels must be unique, ignoring case.");
        RuleFor(x => x.Deck).NotNull().WithMessage("The seat's deck is required.");
        RuleFor(x => x.Deck).Must(BeDeclared).When(x => x.Deck != null)
            .WithMessage("The seat's deck must be one of the bus's decks.");
        RuleFor(x => x.Row).NotNull().WithMessage("The seat's row is required.");
        RuleFor(x => x.Row).Must(BeInsideRows).When(x => x.Row != null)
            .WithMessage("The seat's row must be within its deck's rows.");
        RuleFor(x => x.Column).NotNull().WithMessage("The seat's column is required.");
        RuleFor(x => x.Column).Must(BeInsideColumns).When(x => x.Column != null)
            .WithMessage("The seat's column must be within its deck's columns.");
        RuleFor(x => x.Column).Must(BeOnlySeatHere).When(IsPlaced)
            .WithMessage("Another seat is already at this deck, row and column.");
        RuleFor(x => x.SeatClass).Must(BeOneOfTheBusClasses)
            .WithMessage("The seat class must be one of the bus's seat classes.");
    }

    private static bool IsPlaced(SeatLayoutSeatInput seat)
    {
        return seat.Deck != null && seat.Row != null && seat.Column != null;
    }

    private bool BeLabelledOnce(SeatLayoutSeatInput seat, string? label)
    {
        int labellings = _layout.Seats!.Count(other => other.Label != null && BusRules.IsSameSeatLabel(other.Label, label!));
        return labellings == 1;
    }

    private bool BeDeclared(SeatLayoutSeatInput seat, Deck? deck)
    {
        return DeckOf(seat) != null;
    }

    private bool BeInsideRows(SeatLayoutSeatInput seat, int? row)
    {
        SeatLayoutDeckInput? deck = DeckOf(seat);
        if (deck?.Rows is null)
        {
            return true;
        }

        return BusRules.IsInsideGrid(row!.Value, deck.Rows.Value);
    }

    private bool BeInsideColumns(SeatLayoutSeatInput seat, int? column)
    {
        SeatLayoutDeckInput? deck = DeckOf(seat);
        if (deck?.Columns is null)
        {
            return true;
        }

        return BusRules.IsInsideGrid(column!.Value, deck.Columns.Value);
    }

    private bool BeOnlySeatHere(SeatLayoutSeatInput seat, int? column)
    {
        int occupants = _layout.Seats!.Count(other => other.Deck == seat.Deck && other.Row == seat.Row && other.Column == column);
        return occupants == 1;
    }

    private bool BeOneOfTheBusClasses(SeatLayoutSeatInput seat, string? seatClass)
    {
        if (seatClass is null || _seatClasses is null)
        {
            return false;
        }

        return _seatClasses.Any(busClass => busClass != null && BusRules.IsSameSeatClass(busClass, seatClass));
    }

    // A seat on an undeclared deck is reported once, by the deck rule; the grid rules then have nothing to compare against.
    private SeatLayoutDeckInput? DeckOf(SeatLayoutSeatInput seat)
    {
        if (_layout.Decks is null)
        {
            return null;
        }

        return _layout.Decks.FirstOrDefault(deck => deck.Deck == seat.Deck);
    }
}
