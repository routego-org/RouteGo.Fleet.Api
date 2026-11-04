namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>A range of service dates (IST), both ends inclusive.</summary>
/// <param name="From">The first service date.</param>
/// <param name="To">The last service date.</param>
public sealed record ServiceDateRange(DateOnly From, DateOnly To);
