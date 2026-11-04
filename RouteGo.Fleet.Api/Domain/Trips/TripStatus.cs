namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>
/// A trip's status as reported: Scheduled and Cancelled are stored; Departed is a scheduled trip
/// whose departure time has passed, derived when the trip is read and never stored.
/// </summary>
public enum TripStatus
{
    Scheduled,
    Departed,
    Cancelled,
}
