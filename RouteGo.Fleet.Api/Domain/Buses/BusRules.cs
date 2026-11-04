using System.Text.RegularExpressions;

namespace RouteGo.Fleet.Api.Domain.Buses;

/// <summary>
/// The rules every bus obeys, whichever operation writes it: registration number, name, seat
/// classes, decks and seats. Creating and replacing a bus both validate through these, so a rule
/// is stated once.
/// </summary>
public static partial class BusRules
{
    /// <summary>The most characters a registration number may be sent with, spaces and hyphens included.</summary>
    public const int MaxRegistrationInputLength = 30;

    /// <summary>The fewest characters a registration number has once spaces and hyphens are removed.</summary>
    public const int MinRegistrationLength = 6;

    /// <summary>The most characters a registration number has once spaces and hyphens are removed.</summary>
    public const int MaxRegistrationLength = 15;

    /// <summary>The shortest bus name.</summary>
    public const int MinNameLength = 2;

    /// <summary>The longest bus name.</summary>
    public const int MaxNameLength = 80;

    /// <summary>The most seat classes a bus may have.</summary>
    public const int MaxSeatClasses = 5;

    /// <summary>The shortest seat class name, after trimming.</summary>
    public const int MinSeatClassNameLength = 2;

    /// <summary>The longest seat class name, after trimming.</summary>
    public const int MaxSeatClassNameLength = 30;

    /// <summary>The most decks a bus may have: Lower and Upper.</summary>
    public const int MaxDecks = 2;

    /// <summary>The most rows a deck may have.</summary>
    public const int MaxRows = 15;

    /// <summary>The most columns a deck may have.</summary>
    public const int MaxColumns = 6;

    /// <summary>The most seats a bus may have.</summary>
    public const int MaxSeats = 60;

    /// <summary>
    /// The registration number as RouteGo keeps and compares it: upper case, with the spaces and
    /// hyphens people type removed, so "mh-12 ab 1234" and "MH12AB1234" are the same bus.
    /// </summary>
    public static string NormalizeRegistrationNumber(string registrationNumber)
    {
        string withoutSeparators = registrationNumber.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return withoutSeparators.ToUpperInvariant();
    }

    /// <summary>Letters and digits with optional spaces and hyphens; 6–15 characters once those are removed.</summary>
    public static bool IsAcceptableRegistrationNumber(string? registrationNumber)
    {
        if (registrationNumber is null || registrationNumber.Length > MaxRegistrationInputLength)
        {
            return false;
        }

        if (!RegistrationInput().IsMatch(registrationNumber))
        {
            return false;
        }

        int length = NormalizeRegistrationNumber(registrationNumber).Length;
        return length >= MinRegistrationLength && length <= MaxRegistrationLength;
    }

    /// <summary>A bus name is 2–80 characters.</summary>
    public static bool IsAcceptableName(string? name)
    {
        if (name is null)
        {
            return false;
        }

        return name.Length >= MinNameLength && name.Length <= MaxNameLength;
    }

    /// <summary>A seat class name is 2–30 characters after trimming.</summary>
    public static bool IsAcceptableSeatClassName(string? seatClass)
    {
        if (seatClass is null)
        {
            return false;
        }

        int length = seatClass.Trim().Length;
        return length >= MinSeatClassNameLength && length <= MaxSeatClassNameLength;
    }

    /// <summary>Seat class names are compared trimmed and ignoring case: "Lower" and " lower" are one class.</summary>
    public static bool IsSameSeatClass(string first, string second)
    {
        return string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A seat label is 1–5 letters or digits.</summary>
    public static bool IsAcceptableSeatLabel(string? label)
    {
        if (label is null)
        {
            return false;
        }

        return SeatLabel().IsMatch(label);
    }

    /// <summary>Seat labels are compared ignoring case: "1A" and "1a" are the same seat.</summary>
    public static bool IsSameSeatLabel(string first, string second)
    {
        return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A deck has 1–15 rows.</summary>
    public static bool IsAcceptableRows(int rows)
    {
        return rows >= 1 && rows <= MaxRows;
    }

    /// <summary>A deck has 1–6 columns.</summary>
    public static bool IsAcceptableColumns(int columns)
    {
        return columns >= 1 && columns <= MaxColumns;
    }

    /// <summary>An aisle, when there is one, runs after a column from 1 to one less than the deck's columns.</summary>
    public static bool IsAcceptableAisle(int? aisleAfterColumn, int columns)
    {
        if (aisleAfterColumn is null)
        {
            return true;
        }

        return aisleAfterColumn.Value >= 1 && aisleAfterColumn.Value < columns;
    }

    /// <summary>
    /// A seat sits inside its deck's grid: its row is within the deck's rows and its column within
    /// the deck's columns. Checked one dimension at a time so the offending one can be named.
    /// </summary>
    public static bool IsInsideGrid(int position, int extent)
    {
        return position >= 1 && position <= extent;
    }

    /// <summary>One deck is always the Lower one; a second, if any, is the Upper one.</summary>
    public static bool HasLowerDeck(IEnumerable<Deck> decks)
    {
        return decks.Contains(Deck.Lower);
    }

    [GeneratedRegex("^[A-Za-z0-9 -]+$")]
    private static partial Regex RegistrationInput();

    [GeneratedRegex("^[A-Za-z0-9]{1,5}$")]
    private static partial Regex SeatLabel();
}
