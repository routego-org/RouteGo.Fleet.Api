namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>What a seat of one seat class costs on a trip.</summary>
/// <param name="SeatClass">The seat class, spelt as the bus spells it.</param>
/// <param name="Fare">Whole Indian rupees (INR).</param>
public sealed record SeatClassFare(string SeatClass, int Fare);
