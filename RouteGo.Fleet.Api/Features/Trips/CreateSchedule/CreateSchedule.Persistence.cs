namespace RouteGo.Fleet.Api.Features.Trips.CreateSchedule;

// Reading what the new trips are built from, and writing the schedule, its fares, its trips and
// their events — all on the request's one transaction.
public sealed partial class CreateScheduleHandler
{
    private const string SelectLayoutSql = """
        SELECT name
        FROM buses.seat_classes
        WHERE bus_id = @BusId
        ORDER BY display_order;

        SELECT label, deck, row::int AS row, seat_column::int AS seat_column, seat_class
        FROM buses.bus_seats
        WHERE bus_id = @BusId
        ORDER BY deck, row, seat_column;
        """;

    private const string SelectStopsSql = """
        SELECT s.sequence::int AS sequence, s.city_id, c.name AS city_name, s.offset_minutes::int AS offset_minutes
        FROM routes.route_stops s
        JOIN routes.cities c ON c.city_id = s.city_id
        WHERE s.route_id = @RouteId
        ORDER BY s.sequence
        """;

    // Cancelled trips free the bus; departed ones still hold it until they arrive. Only trips that
    // could reach into the new trips' window, buffer included, are read.
    private const string SelectBusTripsSql = """
        SELECT trip_id, departure_at, arrival_at
        FROM trips.trips
        WHERE bus_id = @BusId AND status = 'Scheduled' AND departure_at < @BusyUntil AND arrival_at > @BusyFrom
        """;

    private const string InsertScheduleSql = """
        INSERT INTO trips.schedules (schedule_id, operator_id, route_id, bus_id, departure_time, days_of_week, status, trips_generated_through)
        VALUES (@ScheduleId, @OperatorId, @RouteId, @BusId, @DepartureTime, @DaysOfWeek, @Status, @TripsGeneratedThrough)
        RETURNING created_at, updated_at
        """;

    private const string InsertScheduleFaresSql = """
        INSERT INTO trips.schedule_class_fares (schedule_id, seat_class, fare)
        SELECT @ScheduleId, f.seat_class, f.fare
        FROM unnest(@SeatClasses, @Fares) AS f(seat_class, fare)
        """;

    private const string InsertTripsSql = """
        INSERT INTO trips.trips (trip_id, schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status)
        SELECT t.trip_id, @ScheduleId, @OperatorId, @RouteId, @BusId, t.service_date, t.departure_at, t.arrival_at, 'Scheduled'
        FROM unnest(@TripIds, @ServiceDates, @DepartureAts, @ArrivalAts) AS t(trip_id, service_date, departure_at, arrival_at)
        """;

    // Every trip starts with the schedule's fare for every class.
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

    private static async Task<BusLayout> GetBusLayoutAsync(IWriterConnection writer, Guid busId, CancellationToken cancellationToken)
    {
        await using SqlMapper.GridReader results = await writer.Connection.QueryMultipleAsync(new CommandDefinition(
            SelectLayoutSql, new { BusId = busId }, writer.Transaction, cancellationToken: cancellationToken));
        IEnumerable<string> seatClasses = await results.ReadAsync<string>();
        IEnumerable<SeatRow> seats = await results.ReadAsync<SeatRow>();
        return new BusLayout(seatClasses.ToList(), seats.ToList());
    }

    private static async Task<List<StopRow>> ListStopsAsync(IWriterConnection writer, Guid routeId, CancellationToken cancellationToken)
    {
        IEnumerable<StopRow> stops = await writer.Connection.QueryAsync<StopRow>(new CommandDefinition(
            SelectStopsSql, new { RouteId = routeId }, writer.Transaction, cancellationToken: cancellationToken));
        return stops.ToList();
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

    private static async Task<CreateScheduleResponse> InsertAsync(
        IWriterConnection writer, NewSchedule schedule, Generation generation, CancellationToken cancellationToken)
    {
        InsertedSchedule inserted = await InsertScheduleAsync(writer, schedule, generation.GeneratedThrough, cancellationToken);
        IReadOnlyList<ClassFare> classFares = generation.Source.ClassFares;
        var fares = new
        {
            schedule.ScheduleId,
            SeatClasses = classFares.Select(classFare => classFare.SeatClass).ToArray(),
            Fares = classFares.Select(classFare => classFare.Fare * TripRules.PaisePerRupee).ToArray(),
            TripIds = generation.Trips.Select(trip => trip.TripId).ToArray(),
        };
        await ExecuteAsync(writer, InsertScheduleFaresSql, fares, cancellationToken);
        await InsertTripsAsync(writer, schedule, generation.Trips, cancellationToken);
        await ExecuteAsync(writer, InsertTripFaresSql, fares, cancellationToken);
        await InsertEventsAsync(writer, schedule, generation, cancellationToken);
        return ToResponse(schedule, inserted, generation);
    }

    private static async Task<InsertedSchedule> InsertScheduleAsync(
        IWriterConnection writer, NewSchedule schedule, DateOnly generatedThrough, CancellationToken cancellationToken)
    {
        var row = new
        {
            schedule.ScheduleId,
            schedule.OperatorId,
            schedule.RouteId,
            schedule.BusId,
            schedule.DepartureTime,
            DaysOfWeek = schedule.DaysOfWeek.Select(day => day.ToString()).ToArray(),
            Status = nameof(ScheduleStatus.Active),
            TripsGeneratedThrough = generatedThrough,
        };
        return await writer.Connection.QuerySingleAsync<InsertedSchedule>(new CommandDefinition(
            InsertScheduleSql, row, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static async Task InsertTripsAsync(
        IWriterConnection writer, NewSchedule schedule, IReadOnlyList<PlannedTrip> trips, CancellationToken cancellationToken)
    {
        var rows = new
        {
            schedule.ScheduleId,
            schedule.OperatorId,
            schedule.RouteId,
            schedule.BusId,
            TripIds = trips.Select(trip => trip.TripId).ToArray(),
            ServiceDates = trips.Select(trip => trip.ServiceDate).ToArray(),
            DepartureAts = trips.Select(trip => trip.DepartureAt).ToArray(),
            ArrivalAts = trips.Select(trip => trip.ArrivalAt).ToArray(),
        };
        await ExecuteAsync(writer, InsertTripsSql, rows, cancellationToken);
    }

    private static async Task InsertEventsAsync(
        IWriterConnection writer, NewSchedule schedule, Generation generation, CancellationToken cancellationToken)
    {
        TripSnapshotSource snapshotSource = ToSnapshotSource(schedule, generation.Source);
        var data = new List<string>();
        foreach (PlannedTrip trip in generation.Trips)
        {
            string snapshot = TripScheduledEvent.ToData(snapshotSource, trip);
            data.Add(snapshot);
        }

        var events = new
        {
            EventType = TripScheduledEvent.EventType,
            schedule.CorrelationId,
            generation.RecordedAt,
            EventIds = generation.Trips.Select(_ => Guid.NewGuid()).ToArray(),
            TripIds = generation.Trips.Select(trip => trip.TripId).ToArray(),
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

    private static TripSnapshotSource ToSnapshotSource(NewSchedule schedule, ScheduleSource source)
    {
        OperatorRow profile = source.Owner.Operator;
        BusRow bus = source.Owner.Bus;
        string[] amenities = bus.Amenities ?? Array.Empty<string>();
        return new TripSnapshotSource
        {
            ScheduleId = schedule.ScheduleId,
            OperatorId = schedule.OperatorId,
            OperatorName = profile.CompanyName,
            OperatorEmail = profile.Email,
            Rating = profile.Rating,
            BusId = schedule.BusId,
            BusName = bus.Name,
            BusType = bus.BusType,
            IsAc = bus.IsAc,
            Amenities = amenities.Select(Enum.Parse<Amenity>).ToList(),
            Seats = source.Layout.Seats.Select(ToSnapshotSeat).ToList(),
            Stops = source.Stops.Select(ToSnapshotStop).ToList(),
            ClassFares = source.ClassFares.Select(classFare => new SeatClassFare(classFare.SeatClass, classFare.Fare)).ToList(),
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

    private static CreateScheduleResponse ToResponse(NewSchedule schedule, InsertedSchedule inserted, Generation generation)
    {
        return new CreateScheduleResponse
        {
            ScheduleId = schedule.ScheduleId,
            OperatorId = schedule.OperatorId,
            RouteId = schedule.RouteId,
            BusId = schedule.BusId,
            DepartureTime = schedule.DepartureTime,
            DaysOfWeek = schedule.DaysOfWeek,
            ClassFares = generation.Source.ClassFares,
            Status = ScheduleStatus.Active,
            TripsGeneratedThrough = generation.GeneratedThrough,
            CreatedAt = ToUtc(inserted.CreatedAt),
            UpdatedAt = ToUtc(inserted.UpdatedAt),
        };
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    /// <summary>The bus's seat classes in display order, and every seat.</summary>
    /// <param name="SeatClasses">The class names as the bus spells them.</param>
    /// <param name="Seats">Every seat, by deck, row and column.</param>
    private sealed record BusLayout(IReadOnlyList<string> SeatClasses, IReadOnlyList<SeatRow> Seats);

    private sealed record InsertedSchedule(DateTime CreatedAt, DateTime UpdatedAt);

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
