namespace RouteGo.Fleet.Api.Domain.Routes;

/// <summary>
/// The rules every route obeys: how many stops, how their offsets run, and how far it goes. A
/// route never changes after creation, so these hold for its whole life.
/// </summary>
public static class RouteRules
{
    /// <summary>The fewest stops: a source and a destination.</summary>
    public const int MinStops = 2;

    /// <summary>The most stops: a source, ten intermediate stops and a destination.</summary>
    public const int MaxStops = 12;

    /// <summary>The longest journey, in minutes after departure: 48 hours.</summary>
    public const int MaxOffsetMinutes = 2880;

    /// <summary>The shortest route, in whole kilometres.</summary>
    public const int MinDistanceKm = 1;

    /// <summary>The longest route, in whole kilometres.</summary>
    public const int MaxDistanceKm = 3000;

    /// <summary>A route has 2 to 12 stops.</summary>
    public static bool IsAcceptableStopCount(int stops)
    {
        return stops >= MinStops && stops <= MaxStops;
    }

    /// <summary>
    /// The source is reached at offset 0; every later stop strictly after the one before it,
    /// and no later than 48 hours after departure.
    /// </summary>
    public static bool IsAcceptableOffset(int position, int offsetMinutes, int? previousOffsetMinutes)
    {
        if (position == 0)
        {
            return offsetMinutes == 0;
        }

        if (offsetMinutes > MaxOffsetMinutes)
        {
            return false;
        }

        return previousOffsetMinutes is null || offsetMinutes > previousOffsetMinutes.Value;
    }

    /// <summary>A route is 1 to 3,000 whole kilometres long.</summary>
    public static bool IsAcceptableDistance(int distanceKm)
    {
        return distanceKm >= MinDistanceKm && distanceKm <= MaxDistanceKm;
    }
}
