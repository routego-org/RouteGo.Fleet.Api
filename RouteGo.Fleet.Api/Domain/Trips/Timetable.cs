namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>When a schedule's trips run: the departure time, the days of service and how long each journey takes.</summary>
/// <param name="Departure">The departure time of day, in IST.</param>
/// <param name="DaysOfWeek">The days of service.</param>
/// <param name="JourneyTime">From departure to arrival at the destination.</param>
public sealed record Timetable(TimeOnly Departure, IReadOnlyList<DayOfWeek> DaysOfWeek, TimeSpan JourneyTime);
