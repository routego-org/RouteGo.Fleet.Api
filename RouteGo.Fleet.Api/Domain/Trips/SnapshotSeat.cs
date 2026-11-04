namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>One seat of a trip's bus, as the trip snapshot needs it.</summary>
public sealed class SnapshotSeat
{
    public required string Label { get; init; }

    public required Deck Deck { get; init; }

    public required int Row { get; init; }

    public required int Column { get; init; }

    /// <summary>The seat class, spelt as the bus spells it.</summary>
    public required string SeatClass { get; init; }
}
