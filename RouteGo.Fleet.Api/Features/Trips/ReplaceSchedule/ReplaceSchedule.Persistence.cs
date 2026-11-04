namespace RouteGo.Fleet.Api.Features.Trips.ReplaceSchedule;

// Reading what the rewrite is decided from, and writing the schedule, its fares, the cancelled,
// re-priced and new trips — all on the request's one transaction.
public sealed partial class ReplaceScheduleHandler
{
    private const string ScheduleChangedReason = "Schedule changed";

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

    // Departed trips and trips the operator cancelled are read too: they are never changed, and
    // they keep their date from getting a new trip. Trips cancelled by an earlier edit or a
    // deactivation leave their date open, so they are not read.
    private const string SelectScheduleTripsSql = """
        SELECT t.trip_id, t.service_date, t.route_id, t.bus_id, t.departure_at, t.status,
               array_agg(f.seat_class ORDER BY f.seat_class) AS fare_classes,
               array_agg(f.fare ORDER BY f.seat_class) AS fares
        FROM trips.trips t
        JOIN trips.trip_class_fares f ON f.trip_id = t.trip_id
        WHERE t.schedule_id = @ScheduleId
          AND t.service_date >= @Today
          AND (t.status = 'Scheduled' OR t.cancellation_cause = 'Operator')
        GROUP BY t.trip_id
        """;

    // Cancelled trips free the bus, including the ones this edit is about to cancel; departed ones
    // still hold it until they arrive. Only trips that could reach into the new trips' window,
    // buffer included, are read.
    private const string SelectBusTripsSql = """
        SELECT trip_id, departure_at, arrival_at
        FROM trips.trips
        WHERE bus_id = @BusId AND status = 'Scheduled' AND departure_at < @BusyUntil AND arrival_at > @BusyFrom
          AND trip_id <> ALL(@CancelledTripIds)
        """;

    // An unchanged schedule writes no audit entry.
    private const string UpdateScheduleSql = """
        UPDATE trips.schedules
        SET route_id = @RouteId, bus_id = @BusId, departure_time = @DepartureTime, days_of_week = @DaysOfWeek
        WHERE schedule_id = @ScheduleId
          AND (route_id, bus_id, departure_time, days_of_week) IS DISTINCT FROM (@RouteId, @BusId, @DepartureTime, @DaysOfWeek)
        """;

    // Classes the new bus does not have (or spells differently) go first, so the upsert never meets
    // a class that differs only in case; an unchanged fare writes no audit entry.
    private const string ReplaceScheduleFaresSql = """
        DELETE FROM trips.schedule_class_fares
        WHERE schedule_id = @ScheduleId AND seat_class <> ALL(@SeatClasses);

        INSERT INTO trips.schedule_class_fares (schedule_id, seat_class, fare)
        SELECT @ScheduleId, f.seat_class, f.fare
        FROM unnest(@SeatClasses, @Fares) AS f(seat_class, fare)
        ON CONFLICT (schedule_id, seat_class) DO UPDATE SET fare = EXCLUDED.fare
        WHERE trips.schedule_class_fares.fare <> EXCLUDED.fare;
        """;

    // Each cancellation records its TripCancelled event in the same statement.
    private const string CancelTripsSql = """
        WITH cancelled AS (
            UPDATE trips.trips
            SET status = 'Cancelled', cancellation_cause = 'ScheduleChanged', cancel_reason = @Reason, cancelled_at = @Now
            WHERE trip_id = ANY(@TripIds) AND status = 'Scheduled'
            RETURNING trip_id
        )
        INSERT INTO trips.fleet_events (event_id, event_type, trip_id, data, correlation_id, recorded_at)
        SELECT gen_random_uuid(), 'TripCancelled', trip_id, jsonb_build_object('tripId', trip_id, 'reason', @Reason),
               @CorrelationId, @Now
        FROM cancelled
        """;

    private const string RepriceTripsSql = """
        UPDATE trips.trip_class_fares f
        SET fare = n.fare
        FROM unnest(@SeatClasses, @Fares) AS n(seat_class, fare)
        WHERE f.trip_id = ANY(@TripIds) AND f.seat_class = n.seat_class AND f.fare <> n.fare
        """;

    private const string InsertTripsSql = """
        INSERT INTO trips.trips (trip_id, schedule_id, operator_id, route_id, bus_id, service_date, departure_at, arrival_at, status)
        SELECT t.trip_id, @ScheduleId, @OperatorId, @RouteId, @BusId, t.service_date, t.departure_at, t.arrival_at, 'Scheduled'
        FROM unnest(@TripIds, @ServiceDates, @DepartureAts, @ArrivalAts) AS t(trip_id, service_date, departure_at, arrival_at)
        """;

    // Every new trip takes the schedule's new fare for every class.
    private const string InsertTripFaresSql = """
        INSERT INTO trips.trip_class_fares (trip_id, seat_class, fare)
        SELECT t.trip_id, f.seat_class, f.fare
        FROM unnest(@TripIds) AS t(trip_id)
        CROSS JOIN unnest(@SeatClasses, @Fares) AS f(seat_class, fare)
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

    private static async Task<List<ScheduleTripRow>> ListScheduleTripsAsync(
        IWriterConnection writer, Replacement replacement, CancellationToken cancellationToken)
    {
        var lookup = new { replacement.Target.ScheduleId, Today = TripRules.GetServiceDate(replacement.Now) };
        IEnumerable<ScheduleTripRow> trips = await writer.Connection.QueryAsync<ScheduleTripRow>(new CommandDefinition(
            SelectScheduleTripsSql, lookup, writer.Transaction, cancellationToken: cancellationToken));
        return trips.ToList();
    }

    private static async Task<List<BusyTrip>> ListBusTripsAsync(
        IWriterConnection writer, RewritePlan plan, CancellationToken cancellationToken)
    {
        var window = new
        {
            plan.Source.Replacement.Details.BusId,
            BusyFrom = plan.NewTrips[0].DepartureAt - TripRules.TurnaroundBuffer,
            BusyUntil = plan.NewTrips[^1].ArrivalAt + TripRules.TurnaroundBuffer,
            CancelledTripIds = plan.CancelledTripIds.ToArray(),
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

    /// <summary>Writes the schedule and every trip change with its events; returns how many trips were cancelled.</summary>
    private static async Task<int> WriteAsync(IWriterConnection writer, RewritePlan plan, CancellationToken cancellationToken)
    {
        await UpdateScheduleAsync(writer, plan.Source, cancellationToken);

        // Cancelled before the new trips go in: a new trip may take the date of a cancelled one.
        int cancelledTripCount = await CancelTripsAsync(writer, plan, cancellationToken);
        await RepriceTripsAsync(writer, plan, cancellationToken);
        await InsertTripsAsync(writer, plan, cancellationToken);
        return cancelledTripCount;
    }

    private static async Task UpdateScheduleAsync(IWriterConnection writer, ReplacementSource source, CancellationToken cancellationToken)
    {
        NewDetails details = source.Replacement.Details;
        var schedule = new
        {
            source.Replacement.Target.ScheduleId,
            details.RouteId,
            details.BusId,
            details.DepartureTime,
            DaysOfWeek = details.DaysOfWeek.Select(day => day.ToString()).ToArray(),
        };
        await ExecuteAsync(writer, UpdateScheduleSql, schedule, cancellationToken);

        var fares = new
        {
            source.Replacement.Target.ScheduleId,
            SeatClasses = ToSeatClasses(source.ClassFares),
            Fares = ToPaise(source.ClassFares),
        };
        await ExecuteAsync(writer, ReplaceScheduleFaresSql, fares, cancellationToken);
    }

    private static async Task<int> CancelTripsAsync(IWriterConnection writer, RewritePlan plan, CancellationToken cancellationToken)
    {
        if (plan.CancelledTripIds.Count == 0)
        {
            return 0;
        }

        Replacement replacement = plan.Source.Replacement;
        var cancellation = new
        {
            TripIds = plan.CancelledTripIds.ToArray(),
            Reason = ScheduleChangedReason,
            replacement.Now,
            replacement.Target.CorrelationId,
        };
        return await writer.Connection.ExecuteAsync(new CommandDefinition(
            CancelTripsSql, cancellation, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static async Task RepriceTripsAsync(IWriterConnection writer, RewritePlan plan, CancellationToken cancellationToken)
    {
        if (plan.RepricedTripIds.Count == 0)
        {
            return;
        }

        var fares = new
        {
            TripIds = plan.RepricedTripIds.ToArray(),
            SeatClasses = ToSeatClasses(plan.Source.ClassFares),
            Fares = ToPaise(plan.Source.ClassFares),
        };
        await ExecuteAsync(writer, RepriceTripsSql, fares, cancellationToken);
        await InsertFareChangedEventsAsync(writer, plan, cancellationToken);
    }

    private static async Task InsertTripsAsync(IWriterConnection writer, RewritePlan plan, CancellationToken cancellationToken)
    {
        if (plan.NewTrips.Count == 0)
        {
            return;
        }

        Replacement replacement = plan.Source.Replacement;
        Guid[] tripIds = plan.NewTrips.Select(trip => trip.TripId).ToArray();
        var rows = new
        {
            replacement.Target.ScheduleId,
            replacement.Target.OperatorId,
            replacement.Details.RouteId,
            replacement.Details.BusId,
            TripIds = tripIds,
            ServiceDates = plan.NewTrips.Select(trip => trip.ServiceDate).ToArray(),
            DepartureAts = plan.NewTrips.Select(trip => trip.DepartureAt).ToArray(),
            ArrivalAts = plan.NewTrips.Select(trip => trip.ArrivalAt).ToArray(),
        };
        await ExecuteAsync(writer, InsertTripsSql, rows, cancellationToken);

        var fares = new { TripIds = tripIds, SeatClasses = ToSeatClasses(plan.Source.ClassFares), Fares = ToPaise(plan.Source.ClassFares) };
        await ExecuteAsync(writer, InsertTripFaresSql, fares, cancellationToken);
        await InsertTripScheduledEventsAsync(writer, plan, cancellationToken);
    }

    private static async Task ExecuteAsync(
        IWriterConnection writer, string sql, object parameters, CancellationToken cancellationToken)
    {
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            sql, parameters, writer.Transaction, cancellationToken: cancellationToken));
    }

    private static string[] ToSeatClasses(IReadOnlyList<SeatClassFare> classFares)
    {
        return classFares.Select(classFare => classFare.SeatClass).ToArray();
    }

    private static long[] ToPaise(IReadOnlyList<SeatClassFare> classFares)
    {
        return classFares.Select(classFare => classFare.Fare * TripRules.PaisePerRupee).ToArray();
    }

    /// <summary>The bus's seat classes in display order, and every seat.</summary>
    /// <param name="SeatClasses">The class names as the bus spells them.</param>
    /// <param name="Seats">Every seat, by deck, row and column.</param>
    private sealed record BusLayout(IReadOnlyList<string> SeatClasses, IReadOnlyList<SeatRow> Seats);

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
