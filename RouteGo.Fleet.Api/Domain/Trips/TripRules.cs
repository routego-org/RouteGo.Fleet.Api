using System.Globalization;
using System.Text.RegularExpressions;

namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>
/// The rules every schedule and trip obeys, whichever operation writes them: the departure time,
/// the days of service, the fares, how far ahead trips are generated, when a trip departs, and how
/// long it keeps its bus busy.
/// </summary>
public static partial class TripRules
{
    /// <summary>How many days of trips a new schedule gets, starting today in IST.</summary>
    public const int TripGenerationDays = 14;

    /// <summary>How far ahead an extension may generate trips: up to today plus this many days.</summary>
    public const int MaxTripHorizonDays = 60;

    /// <summary>How many service dates a trip list covers when the caller gives no range: today and the 13 days after.</summary>
    public const int DefaultListDays = 14;

    /// <summary>The most service dates one trip list may cover, counting both ends.</summary>
    public const int MaxListDays = 31;

    /// <summary>The most days of service a schedule can have: every day of the week.</summary>
    public const int MaxDaysOfWeek = 7;

    /// <summary>The lowest fare, in whole rupees.</summary>
    public const int MinFare = 50;

    /// <summary>The highest fare, in whole rupees.</summary>
    public const int MaxFare = 10_000;

    /// <summary>The most class fares a schedule can give: one for each of a bus's at most five seat classes.</summary>
    public const int MaxClassFares = 5;

    /// <summary>The shortest reason an operator may give for cancelling a trip.</summary>
    public const int MinCancelReasonLength = 10;

    /// <summary>The longest reason an operator may give for cancelling a trip.</summary>
    public const int MaxCancelReasonLength = 500;

    /// <summary>Fares are stored in paise, the rupee's minor unit.</summary>
    public const long PaisePerRupee = 100;

    private const string DepartureTimeFormat = "HH:mm";

    /// <summary>How long a bus stays busy after a trip arrives, before it can start another.</summary>
    public static readonly TimeSpan TurnaroundBuffer = TimeSpan.FromMinutes(60);

    /// <summary>How long before departure a trip's fares freeze, so customers are not surprised by late price changes.</summary>
    public static readonly TimeSpan FareChangeCutoff = TimeSpan.FromHours(6);

    // India keeps one offset all year (no daylight saving), so IST is a fixed +05:30 and needs no
    // time zone database.
    private static readonly TimeSpan _istOffset = new TimeSpan(5, 30, 0);

    /// <summary>A departure time is <c>HH:mm</c> on the 24-hour clock.</summary>
    public static bool IsAcceptableDepartureTime(string? departureTime)
    {
        if (departureTime is null)
        {
            return false;
        }

        return DepartureTime().IsMatch(departureTime);
    }

    /// <summary>The time of day a departure time names; the value has already passed validation.</summary>
    public static TimeOnly ParseDepartureTime(string departureTime)
    {
        return TimeOnly.ParseExact(departureTime, DepartureTimeFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>A fare is whole rupees, from ₹50 to ₹10,000.</summary>
    public static bool IsAcceptableFare(decimal? fare)
    {
        if (fare is null)
        {
            return false;
        }

        bool isWholeRupees = decimal.Truncate(fare.Value) == fare.Value;
        return isWholeRupees && fare.Value >= MinFare && fare.Value <= MaxFare;
    }

    /// <summary>An operator's cancellation reason is 10 to 500 characters.</summary>
    public static bool IsAcceptableCancelReason(string? reason)
    {
        if (reason is null)
        {
            return false;
        }

        return reason.Length >= MinCancelReasonLength && reason.Length <= MaxCancelReasonLength;
    }

    /// <summary>The calendar date in IST at the given instant.</summary>
    public static DateOnly GetServiceDate(DateTimeOffset instant)
    {
        DateTimeOffset inIst = instant.ToOffset(_istOffset);
        return DateOnly.FromDateTime(inIst.DateTime);
    }

    /// <summary>The last service date a schedule created on <paramref name="today"/> generates trips for.</summary>
    public static DateOnly GetInitialGeneratedThrough(DateOnly today)
    {
        return today.AddDays(TripGenerationDays - 1);
    }

    /// <summary>The latest end date an extension made on <paramref name="today"/> may name.</summary>
    public static DateOnly GetLatestEndDate(DateOnly today)
    {
        return today.AddDays(MaxTripHorizonDays);
    }

    /// <summary>An extension's end date runs from today through the latest end date, inclusive.</summary>
    public static bool IsAcceptableEndDate(DateOnly endDate, DateOnly today)
    {
        return endDate >= today && endDate <= GetLatestEndDate(today);
    }

    /// <summary>
    /// The service dates a trip list covers. Without dates it is today through today plus 13 days;
    /// with only one end, the default 14-day window runs from that end.
    /// </summary>
    public static ServiceDateRange GetListRange(DateOnly today, DateOnly? from, DateOnly? to)
    {
        int lastDayOffset = DefaultListDays - 1;
        if (from != null && to != null)
        {
            return new ServiceDateRange(from.Value, to.Value);
        }

        if (from != null)
        {
            return new ServiceDateRange(from.Value, from.Value.AddDays(lastDayOffset));
        }

        if (to != null)
        {
            return new ServiceDateRange(to.Value.AddDays(-lastDayOffset), to.Value);
        }

        return new ServiceDateRange(today, today.AddDays(lastDayOffset));
    }

    /// <summary>A trip list's range runs forwards and covers at most 31 service dates, counting both ends.</summary>
    public static bool IsAcceptableListRange(DateOnly from, DateOnly to)
    {
        int days = to.DayNumber - from.DayNumber + 1;
        return days >= 1 && days <= MaxListDays;
    }

    /// <summary>When a trip on the service date departs: the date plus the departure time, both in IST.</summary>
    public static DateTimeOffset GetDepartureAt(DateOnly serviceDate, TimeOnly departureTime)
    {
        var inIst = new DateTimeOffset(serviceDate.ToDateTime(departureTime), _istOffset);
        return inIst.ToUniversalTime();
    }

    /// <summary>
    /// Whether two trips of one bus would need it at once: each holds the bus from its departure
    /// until the turnaround buffer after its arrival.
    /// </summary>
    public static bool IsOverlapping(
        DateTimeOffset departureAt, DateTimeOffset arrivalAt, DateTimeOffset otherDepartureAt, DateTimeOffset otherArrivalAt)
    {
        bool startsBeforeOtherIsFree = departureAt < otherArrivalAt + TurnaroundBuffer;
        bool otherStartsBeforeThisIsFree = otherDepartureAt < arrivalAt + TurnaroundBuffer;
        return startsBeforeOtherIsFree && otherStartsBeforeThisIsFree;
    }

    /// <summary>A trip's fares may change only while its departure is more than the fare-change cutoff away.</summary>
    public static bool CanChangeFares(DateTimeOffset departureAt, DateTimeOffset now)
    {
        DateTimeOffset cutoffAt = GetFareChangeCutoffAt(departureAt);
        return now < cutoffAt;
    }

    /// <summary>The instant a trip's fares freeze: the fare-change cutoff before its departure.</summary>
    public static DateTimeOffset GetFareChangeCutoffAt(DateTimeOffset departureAt)
    {
        return departureAt - FareChangeCutoff;
    }

    /// <summary>Days of service in week order, Monday first, the order schedules keep and show them in.</summary>
    public static List<DayOfWeek> InWeekOrder(IEnumerable<DayOfWeek> days)
    {
        return days.Distinct().OrderBy(GetWeekPosition).ToList();
    }

    // DayOfWeek numbers Sunday 0; the week here starts on Monday.
    private static int GetWeekPosition(DayOfWeek day)
    {
        return ((int)day + 6) % 7;
    }

    [GeneratedRegex("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    private static partial Regex DepartureTime();
}
