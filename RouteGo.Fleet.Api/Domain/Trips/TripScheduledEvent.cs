namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>
/// The TripScheduled event: its type name and its data, the full trip snapshot every RouteGo service
/// copies. Events are read outside the Fleet HTTP contract, so enum values keep their PascalCase
/// spelling (Seater, Lower, Wifi), fares are whole rupees and times are UTC.
/// </summary>
public static class TripScheduledEvent
{
    /// <summary>The event type recorded with every generated trip.</summary>
    public const string EventType = "TripScheduled";

    // camelCase member names as the events document spells them; enum values are written as the
    // strings the snapshot already holds, so no enum converter is configured.
    private static readonly JsonSerializerOptions _eventJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    /// <summary>The event data for one generated trip, as JSON.</summary>
    public static string ToData(TripSnapshotSource source, PlannedTrip trip)
    {
        IReadOnlyList<SnapshotStop> stops = source.Stops;
        var snapshot = new TripSnapshot
        {
            TripId = trip.TripId,
            ScheduleId = source.ScheduleId,
            OperatorId = source.OperatorId,
            OperatorName = source.OperatorName,
            OperatorEmail = source.OperatorEmail,
            Rating = source.Rating,
            BusId = source.BusId,
            BusName = source.BusName,
            BusType = source.BusType.ToString(),
            IsAc = source.IsAc,
            Amenities = source.Amenities.Select(amenity => amenity.ToString()).ToList(),
            Source = new City(stops[0].CityId, stops[0].CityName),
            Destination = new City(stops[^1].CityId, stops[^1].CityName),
            Stops = ToStops(stops, trip.DepartureAt),
            ServiceDate = trip.ServiceDate,
            DepartureUtc = trip.DepartureAt.UtcDateTime,
            ArrivalUtc = trip.ArrivalAt.UtcDateTime,
            Seats = ToSeats(source.Seats, source.ClassFares),
            TotalSeats = source.Seats.Count,
        };
        return JsonSerializer.Serialize(snapshot, _eventJson);
    }

    private static List<Stop> ToStops(IReadOnlyList<SnapshotStop> stops, DateTimeOffset departureAt)
    {
        var snapshotStops = new List<Stop>();
        foreach (SnapshotStop stop in stops)
        {
            DateTimeOffset arrivalAt = departureAt.AddMinutes(stop.OffsetMinutes);
            snapshotStops.Add(new Stop
            {
                Sequence = stop.Sequence,
                CityId = stop.CityId,
                CityName = stop.CityName,
                ArrivalUtc = arrivalAt.UtcDateTime,
            });
        }

        return snapshotStops;
    }

    // Seats and class fares both carry the bus's own spelling of the class, so they match exactly.
    private static List<Seat> ToSeats(IReadOnlyList<SnapshotSeat> seats, IReadOnlyList<SeatClassFare> classFares)
    {
        var fareByClass = classFares.ToDictionary(classFare => classFare.SeatClass, classFare => classFare.Fare);
        var snapshotSeats = new List<Seat>();
        foreach (SnapshotSeat seat in seats)
        {
            snapshotSeats.Add(new Seat
            {
                SeatNo = seat.Label,
                Deck = seat.Deck.ToString(),
                Row = seat.Row,
                Col = seat.Column,
                Class = seat.SeatClass,
                Fare = fareByClass[seat.SeatClass],
            });
        }

        return snapshotSeats;
    }

    private sealed class TripSnapshot
    {
        public required Guid TripId { get; init; }

        public required Guid ScheduleId { get; init; }

        public required string OperatorId { get; init; }

        public required string OperatorName { get; init; }

        public required string OperatorEmail { get; init; }

        public required decimal Rating { get; init; }

        public required Guid BusId { get; init; }

        public required string BusName { get; init; }

        public required string BusType { get; init; }

        public required bool IsAc { get; init; }

        public required IReadOnlyList<string> Amenities { get; init; }

        public required City Source { get; init; }

        public required City Destination { get; init; }

        public required IReadOnlyList<Stop> Stops { get; init; }

        public required DateOnly ServiceDate { get; init; }

        public required DateTime DepartureUtc { get; init; }

        public required DateTime ArrivalUtc { get; init; }

        public required IReadOnlyList<Seat> Seats { get; init; }

        public required int TotalSeats { get; init; }
    }

    private sealed record City(Guid CityId, string Name);

    private sealed class Stop
    {
        public required int Sequence { get; init; }

        public required Guid CityId { get; init; }

        public required string CityName { get; init; }

        public required DateTime ArrivalUtc { get; init; }
    }

    // SeatNo, Col and Class are the member names the events document gives every subscriber.
    private sealed class Seat
    {
        public required string SeatNo { get; init; }

        public required string Deck { get; init; }

        public required int Row { get; init; }

        public required int Col { get; init; }

        public required string Class { get; init; }

        public required int Fare { get; init; }
    }
}
