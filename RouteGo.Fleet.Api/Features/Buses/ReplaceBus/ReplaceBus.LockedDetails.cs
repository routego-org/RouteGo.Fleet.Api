namespace RouteGo.Fleet.Api.Features.Buses.ReplaceBus;

/// <summary>
/// Decides whether a replacement keeps a bus's seat classes and layout, the details that are locked
/// while the bus has future trips. They count as unchanged when equal to the stored ones: seat
/// classes compared ignoring case and order, decks by deck and grid, seats by label, deck, row,
/// column and class.
/// </summary>
public static class LockedDetails
{
    /// <summary>Whether the replacement's seat classes and layout equal the stored ones.</summary>
    public static bool IsSameLayout(
        IReadOnlyList<string> storedClasses, SeatLayout storedLayout, IReadOnlyList<string> seatClasses, SeatLayout layout)
    {
        return HaveSameSeatClasses(storedClasses, seatClasses)
            && HaveSameDecks(storedLayout.Decks, layout.Decks)
            && HaveSameSeats(storedLayout.Seats, layout.Seats);
    }

    // Each side is a set (classes and labels are unique ignoring case, decks unique), so equal
    // counts plus every replacement item matching a stored one is set equality.
    private static bool HaveSameSeatClasses(IReadOnlyList<string> stored, IReadOnlyList<string> replacement)
    {
        if (stored.Count != replacement.Count)
        {
            return false;
        }

        return replacement.All(seatClass => stored.Any(storedClass => BusRules.IsSameSeatClass(storedClass, seatClass)));
    }

    private static bool HaveSameDecks(IReadOnlyList<SeatLayoutDeck> stored, IReadOnlyList<SeatLayoutDeck> replacement)
    {
        if (stored.Count != replacement.Count)
        {
            return false;
        }

        return replacement.All(deck => stored.Any(storedDeck => IsSameDeck(storedDeck, deck)));
    }

    private static bool IsSameDeck(SeatLayoutDeck stored, SeatLayoutDeck replacement)
    {
        return stored.Deck == replacement.Deck
            && stored.Rows == replacement.Rows
            && stored.Columns == replacement.Columns
            && stored.AisleAfterColumn == replacement.AisleAfterColumn;
    }

    private static bool HaveSameSeats(IReadOnlyList<SeatLayoutSeat> stored, IReadOnlyList<SeatLayoutSeat> replacement)
    {
        if (stored.Count != replacement.Count)
        {
            return false;
        }

        return replacement.All(seat => stored.Any(storedSeat => IsSameSeat(storedSeat, seat)));
    }

    private static bool IsSameSeat(SeatLayoutSeat stored, SeatLayoutSeat replacement)
    {
        return BusRules.IsSameSeatLabel(stored.Label, replacement.Label)
            && stored.Deck == replacement.Deck
            && stored.Row == replacement.Row
            && stored.Column == replacement.Column
            && BusRules.IsSameSeatClass(stored.SeatClass, replacement.SeatClass);
    }
}
