namespace RouteGo.Fleet.Api.Features.Trips.ReplaceSchedule;

// The events the rewrite records with its trip changes: TripScheduled for every new trip and
// FareChanged for every re-priced one (TripCancelled is written by the cancellation itself).
public sealed partial class ReplaceScheduleHandler
{
    private const string InsertEventsSql = """
        INSERT INTO trips.fleet_events (event_id, event_type, trip_id, data, correlation_id, recorded_at)
        SELECT e.event_id, @EventType, e.trip_id, e.data::jsonb, @CorrelationId, @RecordedAt
        FROM unnest(@EventIds, @TripIds, @Data) AS e(event_id, trip_id, data)
        """;

    private static async Task InsertTripScheduledEventsAsync(
        IWriterConnection writer, RewritePlan plan, CancellationToken cancellationToken)
    {
        TripSnapshotSource snapshotSource = ToSnapshotSource(plan.Source);
        var data = new List<string>();
        foreach (PlannedTrip trip in plan.NewTrips)
        {
            string snapshot = TripScheduledEvent.ToData(snapshotSource, trip);
            data.Add(snapshot);
        }

        Guid[] tripIds = plan.NewTrips.Select(trip => trip.TripId).ToArray();
        var batch = new EventBatch(TripScheduledEvent.EventType, tripIds, data);
        await InsertEventsAsync(writer, batch, plan, cancellationToken);
    }

    private static async Task InsertFareChangedEventsAsync(
        IWriterConnection writer, RewritePlan plan, CancellationToken cancellationToken)
    {
        var seats = plan.Source.Layout.Seats.Select(ToSnapshotSeat).ToList();
        var data = new List<string>();
        foreach (Guid tripId in plan.RepricedTripIds)
        {
            string fareChange = FareChangedEvent.ToData(tripId, seats, plan.Source.ClassFares);
            data.Add(fareChange);
        }

        Guid[] tripIds = plan.RepricedTripIds.ToArray();
        var batch = new EventBatch(FareChangedEvent.EventType, tripIds, data);
        await InsertEventsAsync(writer, batch, plan, cancellationToken);
    }

    private static async Task InsertEventsAsync(
        IWriterConnection writer, EventBatch batch, RewritePlan plan, CancellationToken cancellationToken)
    {
        Replacement replacement = plan.Source.Replacement;
        var events = new
        {
            batch.EventType,
            replacement.Target.CorrelationId,
            RecordedAt = replacement.Now,
            EventIds = batch.TripIds.Select(_ => Guid.NewGuid()).ToArray(),
            batch.TripIds,
            Data = batch.Data.ToArray(),
        };
        await ExecuteAsync(writer, InsertEventsSql, events, cancellationToken);
    }

    private static TripSnapshotSource ToSnapshotSource(ReplacementSource source)
    {
        Replacement replacement = source.Replacement;
        string[] amenities = source.Bus.Amenities ?? Array.Empty<string>();
        return new TripSnapshotSource
        {
            ScheduleId = replacement.Target.ScheduleId,
            OperatorId = replacement.Target.OperatorId,
            OperatorName = replacement.Profile.CompanyName,
            OperatorEmail = replacement.Profile.Email,
            Rating = replacement.Profile.Rating,
            BusId = replacement.Details.BusId,
            BusName = source.Bus.Name,
            BusType = source.Bus.BusType,
            IsAc = source.Bus.IsAc,
            Amenities = amenities.Select(Enum.Parse<Amenity>).ToList(),
            Seats = source.Layout.Seats.Select(ToSnapshotSeat).ToList(),
            Stops = source.Stops.Select(ToSnapshotStop).ToList(),
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

    /// <summary>One event per trip, all of one type.</summary>
    /// <param name="EventType">The event type.</param>
    /// <param name="TripIds">The trips, one event each.</param>
    /// <param name="Data">Each trip's event data as JSON, in the same order.</param>
    private sealed record EventBatch(string EventType, Guid[] TripIds, IReadOnlyList<string> Data);
}
