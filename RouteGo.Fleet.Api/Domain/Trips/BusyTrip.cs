namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>A trip that holds its bus from departure until the turnaround buffer after arrival.</summary>
/// <param name="TripId">The trip.</param>
/// <param name="DepartureAt">When it departs.</param>
/// <param name="ArrivalAt">When it arrives at the destination.</param>
public sealed record BusyTrip(Guid TripId, DateTimeOffset DepartureAt, DateTimeOffset ArrivalAt);
