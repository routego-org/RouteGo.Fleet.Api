namespace RouteGo.Fleet.Api.Features.Trips.ReplaceTripFares;

// Reading the bus's classes and seats and the trip's fares, writing the new fares and their event,
// and reading the trip back — all on the request's one transaction.
public sealed partial class ReplaceTripFaresHandler
{
    private const string SelectStoredFaresSql = """
        SELECT name
        FROM buses.seat_classes
        WHERE bus_id = @BusId
        ORDER BY display_order;

        SELECT label, deck, row::int AS row, seat_column::int AS seat_column, seat_class
        FROM buses.bus_seats
        WHERE bus_id = @BusId
        ORDER BY deck, row, seat_column;

        SELECT seat_class, fare
        FROM trips.trip_class_fares
        WHERE trip_id = @TripId;
        """;

    // Only fares that differ are written, so no audit entry records an unchanged fare.
    private const string UpdateFaresSql = """
        UPDATE trips.trip_class_fares f
        SET fare = n.fare
        FROM unnest(@SeatClasses, @Fares) AS n(seat_class, fare)
        WHERE f.trip_id = @TripId AND f.seat_class = n.seat_class AND f.fare <> n.fare
        """;

    private const string InsertEventSql = """
        INSERT INTO trips.fleet_events (event_id, event_type, trip_id, data, correlation_id, recorded_at)
        VALUES (@EventId, @EventType, @TripId, @Data::jsonb, @CorrelationId, @RecordedAt)
        """;

    // The source and destination are the route's first and last stops.
    private const string SelectTripSql = """
        SELECT t.trip_id, t.schedule_id, t.operator_id, p.company_name AS operator_name, t.route_id,
               src.city_id AS source_city_id, src.name AS source_name,
               dst.city_id AS destination_city_id, dst.name AS destination_name,
               t.bus_id, b.name AS bus_name, t.service_date, t.departure_at, t.arrival_at
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

    private static async Task<StoredFares> GetStoredFaresAsync(
        IWriterConnection writer, Repricing repricing, CancellationToken cancellationToken)
    {
        var lookup = new { repricing.Trip.BusId, repricing.Target.TripId };
        await using SqlMapper.GridReader results = await writer.Connection.QueryMultipleAsync(new CommandDefinition(
            SelectStoredFaresSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        IEnumerable<string> seatClasses = await results.ReadAsync<string>();
        IEnumerable<SeatRow> seats = await results.ReadAsync<SeatRow>();
        IEnumerable<FareRow> tripFares = await results.ReadAsync<FareRow>();
        return new StoredFares
        {
            SeatClasses = seatClasses.ToList(),
            Seats = seats.Select(ToSnapshotSeat).ToList(),
            TripFares = tripFares.ToList(),
        };
    }

    private static async Task UpdateFaresAsync(
        IWriterConnection writer, Repricing repricing, FareChange fareChange, CancellationToken cancellationToken)
    {
        var fares = new
        {
            repricing.Target.TripId,
            SeatClasses = fareChange.ClassFares.Select(classFare => classFare.SeatClass).ToArray(),
            Fares = fareChange.ClassFares.Select(classFare => classFare.Fare * TripRules.PaisePerRupee).ToArray(),
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            UpdateFaresSql, fares, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static async Task InsertFareChangedEventAsync(
        IWriterConnection writer, Repricing repricing, FareChange fareChange, CancellationToken cancellationToken)
    {
        string data = FareChangedEvent.ToData(repricing.Target.TripId, fareChange.Seats, fareChange.ClassFares);
        var fareChanged = new
        {
            EventId = Guid.NewGuid(),
            FareChangedEvent.EventType,
            repricing.Target.TripId,
            Data = data,
            repricing.Target.CorrelationId,
            RecordedAt = repricing.Now,
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertEventSql, fareChanged, writer.Transaction, cancellationToken: cancellationToken));
    }

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
            Status = TripStatus.Scheduled,
            ClassFares = classFares,
            CancelReason = null,
            CancelledAt = null,
        };
    }

    private static SnapshotSeat ToSnapshotSeat(SeatRow seat)
    {
        return new SnapshotSeat { Label = seat.Label, Deck = seat.Deck, Row = seat.Row, Column = seat.SeatColumn, SeatClass = seat.SeatClass };
    }

    /// <summary>The bus's seat classes in display order, its seats, and the trip's current fares.</summary>
    private sealed class StoredFares
    {
        public required IReadOnlyList<string> SeatClasses { get; init; }

        public required IReadOnlyList<SnapshotSeat> Seats { get; init; }

        public required IReadOnlyList<FareRow> TripFares { get; init; }
    }

    // The table names the column seat_column because COLUMN is a reserved SQL word.
    private sealed class SeatRow
    {
        public string Label { get; init; } = string.Empty;

        public Deck Deck { get; init; }

        public int Row { get; init; }

        public int SeatColumn { get; init; }

        public string SeatClass { get; init; } = string.Empty;
    }

    // Fares are stored in paise and shown in whole rupees.
    private sealed class FareRow
    {
        public string SeatClass { get; init; } = string.Empty;

        public long Fare { get; init; }
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
    }
}
