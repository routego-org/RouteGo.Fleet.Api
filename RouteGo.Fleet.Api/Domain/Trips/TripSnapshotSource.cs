namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>
/// Everything stored that a TripScheduled snapshot is built from, other than the trip itself: the
/// schedule, the operator, the bus with its seats, the route's stops and the fares.
/// </summary>
public sealed class TripSnapshotSource
{
    public required Guid ScheduleId { get; init; }

    public required string OperatorId { get; init; }

    /// <summary>The operator's company name.</summary>
    public required string OperatorName { get; init; }

    public required string OperatorEmail { get; init; }

    public required decimal Rating { get; init; }

    public required Guid BusId { get; init; }

    public required string BusName { get; init; }

    public required BusType BusType { get; init; }

    public required bool IsAc { get; init; }

    public required IReadOnlyList<Amenity> Amenities { get; init; }

    /// <summary>Every seat, by deck, row and column.</summary>
    public required IReadOnlyList<SnapshotSeat> Seats { get; init; }

    /// <summary>Every stop in order, source first, destination last.</summary>
    public required IReadOnlyList<SnapshotStop> Stops { get; init; }

    /// <summary>One fare for every seat class of the bus.</summary>
    public required IReadOnlyList<SeatClassFare> ClassFares { get; init; }
}
