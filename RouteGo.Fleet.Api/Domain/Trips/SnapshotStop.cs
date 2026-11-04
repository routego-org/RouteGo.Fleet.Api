namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>One stop of a trip's route, as the trip snapshot needs it.</summary>
public sealed class SnapshotStop
{
    /// <summary>The stop's place in the route, 1 for the source.</summary>
    public required int Sequence { get; init; }

    public required Guid CityId { get; init; }

    public required string CityName { get; init; }

    /// <summary>Minutes after departure at which the stop is reached.</summary>
    public required int OffsetMinutes { get; init; }
}
