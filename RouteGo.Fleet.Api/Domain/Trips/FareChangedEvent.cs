namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>
/// The FareChanged event: its type name and its data — the trip's new fare for every seat class,
/// and every seat with its new fare. Events are read outside the Fleet HTTP contract, so fares are
/// whole rupees and member names follow the events document.
/// </summary>
public static class FareChangedEvent
{
    /// <summary>The event type recorded with every trip whose fares changed.</summary>
    public const string EventType = "FareChanged";

    // camelCase member names as the events document spells them.
    private static readonly JsonSerializerOptions _eventJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    /// <summary>The event data for one re-priced trip, as JSON.</summary>
    /// <param name="tripId">The trip whose fares changed.</param>
    /// <param name="seats">Every seat of the trip's bus.</param>
    /// <param name="classFares">The trip's new fare for every seat class, in the bus's seat-class order.</param>
    public static string ToData(Guid tripId, IReadOnlyList<SnapshotSeat> seats, IReadOnlyList<SeatClassFare> classFares)
    {
        var fareChange = new FareChange
        {
            TripId = tripId,
            ClassFares = classFares.Select(classFare => new ClassFare(classFare.SeatClass, classFare.Fare)).ToList(),
            Seats = ToSeats(seats, classFares),
        };
        return JsonSerializer.Serialize(fareChange, _eventJson);
    }

    // Seats and class fares both carry the bus's own spelling of the class, so they match exactly.
    private static List<Seat> ToSeats(IReadOnlyList<SnapshotSeat> seats, IReadOnlyList<SeatClassFare> classFares)
    {
        var fareByClass = classFares.ToDictionary(classFare => classFare.SeatClass, classFare => classFare.Fare);
        var fareSeats = new List<Seat>();
        foreach (SnapshotSeat seat in seats)
        {
            fareSeats.Add(new Seat(seat.Label, seat.SeatClass, fareByClass[seat.SeatClass]));
        }

        return fareSeats;
    }

    private sealed class FareChange
    {
        public required Guid TripId { get; init; }

        public required IReadOnlyList<ClassFare> ClassFares { get; init; }

        public required IReadOnlyList<Seat> Seats { get; init; }
    }

    // Class and SeatNo are the member names the events document gives every subscriber.
    private sealed record ClassFare(string Class, int Fare);

    private sealed record Seat(string SeatNo, string Class, int Fare);
}
