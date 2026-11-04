namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>A trip about to be generated, with its identifier assigned up front so a conflict or event can name it.</summary>
public sealed class PlannedTrip
{
    public required Guid TripId { get; init; }

    /// <summary>The calendar date (IST) on which it departs.</summary>
    public required DateOnly ServiceDate { get; init; }

    public required DateTimeOffset DepartureAt { get; init; }

    public required DateTimeOffset ArrivalAt { get; init; }
}
