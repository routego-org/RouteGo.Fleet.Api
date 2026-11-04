namespace RouteGo.Fleet.Api.Features.Trips.CreateScheduleTrips;

// Reading what the new trips are built from, and writing the trips, their fares, their events and
// the schedule's new generated-through date — all on the request's one transaction.
public sealed partial class CreateScheduleTripsHandler
{
    // A date is taken when the schedule already has a scheduled trip on it, or when the operator
    // cancelled that date's trip: Fleet never brings that trip back. Trips cancelled by a schedule
    // edit or deactivation leave their date open.
    private const string SelectPartsSql = """
        SELECT name
        FROM buses.seat_classes
        WHERE bus_id = @BusId
        ORDER BY display_order;

        SELECT label, deck, row::int AS row, seat_column::int AS seat_column, seat_class
        FROM buses.bus_seats
        WHERE bus_id = @BusId
        ORDER BY deck, row, seat_column;

        SELECT seat_class, fare
        FROM trips.schedule_class_fares
        WHERE schedule_id = @ScheduleId;

        SELECT s.sequence::int AS sequence, s.city_id, c.name AS city_name, s.offset_minutes::int AS offset_minutes
        FROM routes.route_stops s
        JOIN routes.cities c ON c.city_id = s.city_id
        WHERE s.route_id = @RouteId
        ORDER BY s.sequence;

        SELECT service_date
        FROM trips.trips
        WHERE schedule_id = @ScheduleId
          AND service_date BETWEEN @Today AND @EndDate
          AND (status = 'Scheduled' OR cancellation_cause = 'Operator');
        """;

    // Cancelled trips free the bus; departed ones still hold it until they arrive. Only trips that
    // could reach into the new trips' window, buffer included, are read.
    private const string SelectBusTripsSql = """
        SELECT trip_id, departure_at, arrival_at
        FROM trips.trips
        WHERE bus_id = @BusId AND status = 'Scheduled' AND departure_at < @BusyUntil AND arrival_at > @BusyFrom
        """;

    private const string InsertTripsSql = """
        INSERT INTO trips.trips (trip_id, schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status)
        SELECT t.trip_id, @ScheduleId, @OperatorId, @RouteId, @BusId, t.service_date, t.departure_at, t.arrival_at, 'Scheduled'
        FROM unnest(@TripIds, @ServiceDates, @DepartureAts, @ArrivalAts) AS t(trip_id, service_date, departure_at, arrival_at)
        """;

    // Every trip takes the schedule's current fare for every class.
    private const string InsertTripFaresSql = """
        INSERT INTO trips.trip_class_fares (trip_id, seat_class, fare)
        SELECT t.trip_id, f.seat_class, f.fare
        FROM unnest(@TripIds) AS t(trip_id)
        CROSS JOIN unnest(@SeatClasses, @Fares) AS f(seat_class, fare)
        """;

    private const string InsertEventsSql = """
        INSERT INTO trips.fleet_events (event_id, event_type, trip_id, data, correlation_id, recorded_at)
        SELECT e.event_id, @EventType, e.trip_id, e.data::jsonb, @CorrelationId, @RecordedAt
        FROM unnest(@EventIds, @TripIds, @Data) AS e(event_id, trip_id, data)
        """;

    // The generated-through date never moves back, and an unchanged date writes no audit entry.
    private const string MoveGeneratedThroughSql = """
        UPDATE trips.schedules
        SET trips_generated_through = @EndDate
        WHERE schedule_id = @ScheduleId AND trips_generated_through < @EndDate
        """;

    private static async Task<StoredParts> GetPartsAsync(
        IWriterConnection writer, Extension extension, CancellationToken cancellationToken)
    {
        var lookup = new
        {
            extension.Schedule.BusId,
            extension.Schedule.RouteId,
            extension.Target.ScheduleId,
            Today = TripRules.GetServiceDate(extension.Now),
            extension.EndDate,
        };
        await using SqlMapper.GridReader results = await writer.Connection.QueryMultipleAsync(new CommandDefinition(
            SelectPartsSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        IEnumerable<string> seatClasses = await results.ReadAsync<string>();
        IEnumerable<SeatRow> seats = await results.ReadAsync<SeatRow>();
        IEnumerable<StoredFare> fares = await results.ReadAsync<StoredFare>();
        IEnumerable<StopRow> stops = await results.ReadAsync<StopRow>();
        IEnumerable<DateOnly> takenDates = await results.ReadAsync<DateOnly>();
        return new StoredParts
        {
            SeatClasses = seatClasses.ToList(),
            Seats = seats.ToList(),
            Fares = fares.ToList(),
            Stops = stops.ToList(),
            TakenDates = takenDates.ToHashSet(),
        };
    }

    private static async Task<List<BusyTrip>> ListBusTripsAsync(
        IWriterConnection writer, Guid busId, IReadOnlyList<PlannedTrip> planned, CancellationToken cancellationToken)
    {
        var window = new
        {
            BusId = busId,
            BusyFrom = planned[0].DepartureAt - TripRules.TurnaroundBuffer,
            BusyUntil = planned[^1].ArrivalAt + TripRules.TurnaroundBuffer,
        };
        IEnumerable<BusyTripRow> rows = await writer.Connection.QueryAsync<BusyTripRow>(new CommandDefinition(
            SelectBusTripsSql, window, writer.Transaction, cancellationToken: cancellationToken));

        var trips = new List<BusyTrip>();
        foreach (BusyTripRow row in rows)
        {
            trips.Add(new BusyTrip(row.TripId, ToUtc(row.DepartureAt), ToUtc(row.ArrivalAt)));
        }

        return trips;
    }

    /// <summary>Writes the trips, their fares and events, and moves the schedule forward; returns its generated-through date.</summary>
    private static async Task<DateOnly> InsertAsync(
        IWriterConnection writer, ExtensionSource source, IReadOnlyList<PlannedTrip> trips, CancellationToken cancellationToken)
    {
        Extension extension = source.Extension;
        if (trips.Count > 0)
        {
            await InsertTripsAsync(writer, source, trips, cancellationToken);
            await InsertEventsAsync(writer, source, trips, cancellationToken);
        }

        var move = new { extension.Target.ScheduleId, extension.EndDate };
        await ExecuteAsync(writer, MoveGeneratedThroughSql, move, cancellationToken);

        var storedThrough = DateOnly.FromDateTime(extension.Schedule.TripsGeneratedThrough);
        if (extension.EndDate > storedThrough)
        {
            return extension.EndDate;
        }

        return storedThrough;
    }

    private static async Task InsertTripsAsync(
        IWriterConnection writer, ExtensionSource source, IReadOnlyList<PlannedTrip> trips, CancellationToken cancellationToken)
    {
        Extension extension = source.Extension;
        Guid[] tripIds = trips.Select(trip => trip.TripId).ToArray();
        var rows = new
        {
            extension.Target.ScheduleId,
            extension.Target.OperatorId,
            extension.Schedule.RouteId,
            extension.Schedule.BusId,
            TripIds = tripIds,
            ServiceDates = trips.Select(trip => trip.ServiceDate).ToArray(),
            DepartureAts = trips.Select(trip => trip.DepartureAt).ToArray(),
            ArrivalAts = trips.Select(trip => trip.ArrivalAt).ToArray(),
        };
        await ExecuteAsync(writer, InsertTripsSql, rows, cancellationToken);

        var fares = new
        {
            TripIds = tripIds,
            SeatClasses = source.ClassFares.Select(classFare => classFare.SeatClass).ToArray(),
            Fares = source.ClassFares.Select(classFare => classFare.Fare * TripRules.PaisePerRupee).ToArray(),
        };
        await ExecuteAsync(writer, InsertTripFaresSql, fares, cancellationToken);
    }

    private static async Task InsertEventsAsync(
        IWriterConnection writer, ExtensionSource source, IReadOnlyList<PlannedTrip> trips, CancellationToken cancellationToken)
    {
        TripSnapshotSource snapshotSource = ToSnapshotSource(source);
        var data = new List<string>();
        foreach (PlannedTrip trip in trips)
        {
            string snapshot = TripScheduledEvent.ToData(snapshotSource, trip);
            data.Add(snapshot);
        }

        var events = new
        {
            EventType = TripScheduledEvent.EventType,
            source.Extension.Target.CorrelationId,
            RecordedAt = source.Extension.Now,
            EventIds = trips.Select(_ => Guid.NewGuid()).ToArray(),
            TripIds = trips.Select(trip => trip.TripId).ToArray(),
            Data = data.ToArray(),
        };
        await ExecuteAsync(writer, InsertEventsSql, events, cancellationToken);
    }

    private static async Task ExecuteAsync(
        IWriterConnection writer, string sql, object parameters, CancellationToken cancellationToken)
    {
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            sql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static TripSnapshotSource ToSnapshotSource(ExtensionSource source)
    {
        Extension extension = source.Extension;
        string[] amenities = source.Bus.Amenities ?? Array.Empty<string>();
        return new TripSnapshotSource
        {
            ScheduleId = extension.Target.ScheduleId,
            OperatorId = extension.Target.OperatorId,
            OperatorName = extension.Profile.CompanyName,
            OperatorEmail = extension.Profile.Email,
            Rating = extension.Profile.Rating,
            BusId = extension.Schedule.BusId,
            BusName = source.Bus.Name,
            BusType = source.Bus.BusType,
            IsAc = source.Bus.IsAc,
            Amenities = amenities.Select(Enum.Parse<Amenity>).ToList(),
            Seats = source.Parts.Seats.Select(ToSnapshotSeat).ToList(),
            Stops = source.Parts.Stops.Select(ToSnapshotStop).ToList(),
            ClassFares = source.ClassFares,
        };
    }

    private static SnapshotSeat ToSnapshotSeat(SeatRow seat)
    {
        return new SnapshotSeat { Label = seat.Label, Deck = seat.Deck, Row = seat.Row, Column = seat.SeatColumn, SeatClass = seat.SeatClass };
    }

    private static SnapshotStop ToSnapshotStop(StopRow stop)
    {
        return new SnapshotStop { Sequence = stop.Sequence, CityId = stop.CityId, CityName = stop.CityName, OffsetMinutes = stop.OffsetMinutes };
    }

    private static List<Trip> ToTrips(ExtensionSource source, IReadOnlyList<PlannedTrip> planned)
    {
        var classFares = source.ClassFares.Select(classFare => new ClassFare(classFare.SeatClass, classFare.Fare)).ToList();
        var trips = new List<Trip>();
        foreach (PlannedTrip trip in planned)
        {
            trips.Add(ToTrip(source, trip, classFares));
        }

        return trips;
    }

    private static Trip ToTrip(ExtensionSource source, PlannedTrip trip, IReadOnlyList<ClassFare> classFares)
    {
        Extension extension = source.Extension;
        IReadOnlyList<StopRow> stops = source.Parts.Stops;
        return new Trip
        {
            TripId = trip.TripId,
            ScheduleId = extension.Target.ScheduleId,
            OperatorId = extension.Target.OperatorId,
            OperatorName = extension.Profile.CompanyName,
            RouteId = extension.Schedule.RouteId,
            Source = new TripCity(stops[0].CityId, stops[0].CityName),
            Destination = new TripCity(stops[^1].CityId, stops[^1].CityName),
            BusId = extension.Schedule.BusId,
            BusName = source.Bus.Name,
            ServiceDate = trip.ServiceDate,
            DepartureAt = trip.DepartureAt,
            ArrivalAt = trip.ArrivalAt,
            Status = TripStatus.Scheduled,
            ClassFares = classFares,
            CancelReason = null,
            CancelledAt = null,
        };
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    /// <summary>The bus's layout, the schedule's fares, the route's stops, and the dates that already have a trip or must not get one.</summary>
    private sealed class StoredParts
    {
        /// <summary>The bus's seat classes in display order.</summary>
        public required IReadOnlyList<string> SeatClasses { get; init; }

        public required IReadOnlyList<SeatRow> Seats { get; init; }

        public required IReadOnlyList<StoredFare> Fares { get; init; }

        public required IReadOnlyList<StopRow> Stops { get; init; }

        public required IReadOnlySet<DateOnly> TakenDates { get; init; }
    }

    // Fares are stored in paise and shown in whole rupees.
    private sealed class StoredFare
    {
        public string SeatClass { get; init; } = string.Empty;

        public long Fare { get; init; }

        public int Rupees
        {
            get { return (int)(Fare / TripRules.PaisePerRupee); }
        }
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

    private sealed class StopRow
    {
        public int Sequence { get; init; }

        public Guid CityId { get; init; }

        public string CityName { get; init; } = string.Empty;

        public int OffsetMinutes { get; init; }
    }

    private sealed class BusyTripRow
    {
        public Guid TripId { get; init; }

        public DateTime DepartureAt { get; init; }

        public DateTime ArrivalAt { get; init; }
    }
}
