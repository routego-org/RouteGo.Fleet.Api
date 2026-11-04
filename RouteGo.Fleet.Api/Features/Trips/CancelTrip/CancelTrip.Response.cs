namespace RouteGo.Fleet.Api.Features.Trips.CancelTrip;

// Reading the cancelled trip back as stored, so the response shows the reason and time kept with it.
public sealed partial class CancelTripHandler
{
    // The source and destination are the route's first and last stops; fares follow the bus's
    // seat-class order, a class the bus has since dropped last, by name.
    private const string SelectTripSql = """
        SELECT t.trip_id, t.schedule_id, t.operator_id, p.company_name AS operator_name, t.route_id,
               src.city_id AS source_city_id, src.name AS source_name,
               dst.city_id AS destination_city_id, dst.name AS destination_name,
               t.bus_id, b.name AS bus_name, t.service_date, t.departure_at, t.arrival_at,
               t.cancel_reason, t.cancelled_at
        FROM trips.trips t
        JOIN operator_profiles.operator_profiles p ON p.operator_id = t.operator_id
        JOIN buses.buses b ON b.bus_id = t.bus_id
        JOIN LATERAL (SELECT s.city_id, c.name FROM routes.route_stops s JOIN routes.cities c ON c.city_id = s.city_id
                      WHERE s.route_id = t.route_id ORDER BY s.sequence LIMIT 1) src ON true
        JOIN LATERAL (SELECT s.city_id, c.name FROM routes.route_stops s JOIN routes.cities c ON c.city_id = s.city_id
                      WHERE s.route_id = t.route_id ORDER BY s.sequence DESC LIMIT 1) dst ON true
        WHERE t.trip_id = @TripId;

        SELECT f.seat_class, f.fare
        FROM trips.trip_class_fares f
        JOIN trips.trips t ON t.trip_id = f.trip_id
        LEFT JOIN buses.seat_classes c ON c.bus_id = t.bus_id AND c.name = f.seat_class
        WHERE f.trip_id = @TripId
        ORDER BY c.display_order NULLS LAST, f.seat_class;
        """;

    private static async Task<Trip> GetTripAsync(IWriterConnection writer, Guid tripId, CancellationToken cancellationToken)
    {
        await using SqlMapper.GridReader results = await writer.Connection.QueryMultipleAsync(new CommandDefinition(
            SelectTripSql, new { TripId = tripId }, writer.Transaction, cancellationToken: cancellationToken));
        StoredTripRow trip = await results.ReadSingleAsync<StoredTripRow>();
        IEnumerable<FareRow> fares = await results.ReadAsync<FareRow>();
        return ToTrip(trip, fares);
    }

    private static Trip ToTrip(StoredTripRow trip, IEnumerable<FareRow> fares)
    {
        var classFares = new List<ClassFare>();
        foreach (FareRow fare in fares)
        {
            classFares.Add(new ClassFare(fare.SeatClass, (int)(fare.Fare / TripRules.PaisePerRupee)));
        }

        return new Trip
        {
            TripId = trip.TripId,
            ScheduleId = trip.ScheduleId,
            OperatorId = trip.OperatorId,
            OperatorName = trip.OperatorName,
            RouteId = trip.RouteId,
            Source = new TripCity(trip.SourceCityId, trip.SourceName),
            Destination = new TripCity(trip.DestinationCityId, trip.DestinationName),
            BusId = trip.BusId,
            BusName = trip.BusName,
            ServiceDate = DateOnly.FromDateTime(trip.ServiceDate),
            DepartureAt = ToUtc(trip.DepartureAt),
            ArrivalAt = ToUtc(trip.ArrivalAt),
            Status = TripStatus.Cancelled,
            ClassFares = classFares,
            CancelReason = trip.CancelReason,
            CancelledAt = ToUtc(trip.CancelledAt),
        };
    }

    private sealed class StoredTripRow
    {
        public Guid TripId { get; init; }

        public Guid ScheduleId { get; init; }

        public string OperatorId { get; init; } = string.Empty;

        public string OperatorName { get; init; } = string.Empty;

        public Guid RouteId { get; init; }

        public Guid SourceCityId { get; init; }

        public string SourceName { get; init; } = string.Empty;

        public Guid DestinationCityId { get; init; }

        public string DestinationName { get; init; } = string.Empty;

        public Guid BusId { get; init; }

        public string BusName { get; init; } = string.Empty;

        public DateTime ServiceDate { get; init; }

        public DateTime DepartureAt { get; init; }

        public DateTime ArrivalAt { get; init; }

        public string CancelReason { get; init; } = string.Empty;

        public DateTime CancelledAt { get; init; }
    }

    // Fares are stored in paise and shown in whole rupees.
    private sealed class FareRow
    {
        public string SeatClass { get; init; } = string.Empty;

        public long Fare { get; init; }
    }
}
