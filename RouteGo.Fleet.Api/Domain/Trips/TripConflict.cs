namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>A trip a request would have put its bus on while the bus is busy with another trip.</summary>
/// <param name="ServiceDate">The service date (IST) of the trip the request would have produced.</param>
/// <param name="TripId">The other trip that already holds the bus at that time.</param>
/// <param name="DepartureAt">When that other trip departs.</param>
public sealed record TripConflict(DateOnly ServiceDate, Guid TripId, DateTimeOffset DepartureAt);
