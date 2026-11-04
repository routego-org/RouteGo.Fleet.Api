namespace RouteGo.Fleet.Api.Features.Trips.ReplaceSchedule;

// Deciding how the schedule's trips follow its new details: which future trips are cancelled,
// which are re-priced, which dates get a new trip — and refusing the whole edit on any overlap.
public sealed partial class ReplaceScheduleHandler
{
    private static async Task<ReplaceScheduleResult> RewriteAsync(
        IWriterConnection writer, ReplacementSource source, CancellationToken cancellationToken)
    {
        List<ScheduleTripRow> trips = await ListScheduleTripsAsync(writer, source.Replacement, cancellationToken);
        RewritePlan plan = PlanRewrite(trips, source);
        List<TripConflict> conflicts = await ListConflictsAsync(writer, plan, cancellationToken);
        if (conflicts.Count > 0)
        {
            return new ReplaceScheduleResult { Outcome = ReplaceScheduleOutcome.BusTripOverlap, Conflicts = conflicts };
        }

        int cancelledTripCount = await WriteAsync(writer, plan, cancellationToken);
        Schedule schedule = await GetScheduleAsync(writer, source.Replacement.Target.ScheduleId, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        var response = new ReplaceScheduleResponse
        {
            Schedule = schedule,
            CreatedTripCount = plan.NewTrips.Count,
            CancelledTripCount = cancelledTripCount,
            RepricedTripCount = plan.RepricedTripIds.Count,
        };
        return new ReplaceScheduleResult { Outcome = ReplaceScheduleOutcome.Replaced, Response = response };
    }

    // The trips the edit keeps or generates must not overlap any other trip of the bus; the ones it
    // cancels free the bus. Nothing generated means nothing new can overlap.
    private static async Task<List<TripConflict>> ListConflictsAsync(
        IWriterConnection writer, RewritePlan plan, CancellationToken cancellationToken)
    {
        if (plan.NewTrips.Count == 0)
        {
            return new List<TripConflict>();
        }

        List<BusyTrip> busTrips = await ListBusTripsAsync(writer, plan, cancellationToken);
        return TripPlan.FindConflicts(plan.NewTrips, busTrips);
    }

    // Every service date from today through the generated-through date that is a day of service and
    // has no scheduled trip left gets a new trip, unless the operator cancelled that date's trip.
    private static RewritePlan PlanRewrite(IReadOnlyList<ScheduleTripRow> trips, ReplacementSource source)
    {
        Replacement replacement = source.Replacement;
        var plan = new RewritePlan { Source = source };
        foreach (ScheduleTripRow trip in trips)
        {
            TripFate fate = GetFate(trip, source);
            if (fate == TripFate.Cancelled)
            {
                plan.CancelledTripIds.Add(trip.TripId);
                continue;
            }

            plan.TakenDates.Add(DateOnly.FromDateTime(trip.ServiceDate));
            if (fate == TripFate.Repriced)
            {
                plan.RepricedTripIds.Add(trip.TripId);
            }
        }

        var generatedThrough = DateOnly.FromDateTime(replacement.Schedule.TripsGeneratedThrough);
        List<PlannedTrip> newTrips = TripPlan.GenerateTrips(replacement.Now, ToTimetable(source), generatedThrough, plan.TakenDates);
        plan.NewTrips.AddRange(newTrips);
        return plan;
    }

    // Departed trips and trips the operator cancelled are never changed. A future trip that no
    // longer follows the schedule is cancelled; one that differs only in fares takes the new fares
    // unless they are already frozen before its departure.
    private static TripFate GetFate(ScheduleTripRow trip, ReplacementSource source)
    {
        DateTimeOffset now = source.Replacement.Now;
        DateTimeOffset departureAt = ToUtc(trip.DepartureAt);
        bool isFuture = trip.Status == TripStatus.Scheduled && departureAt > now;
        if (!isFuture)
        {
            return TripFate.Kept;
        }

        if (!FollowsDetails(trip, source.Replacement.Details))
        {
            return TripFate.Cancelled;
        }

        bool hasNewFares = !HasFares(trip, source.ClassFares);
        if (hasNewFares && TripRules.CanChangeFares(departureAt, now))
        {
            return TripFate.Repriced;
        }

        return TripFate.Kept;
    }

    private static bool FollowsDetails(ScheduleTripRow trip, NewDetails details)
    {
        var serviceDate = DateOnly.FromDateTime(trip.ServiceDate);
        DateTimeOffset departureAt = TripRules.GetDepartureAt(serviceDate, details.Departure);
        bool isSameRun = trip.RouteId == details.RouteId && trip.BusId == details.BusId;
        bool isSameDeparture = ToUtc(trip.DepartureAt) == departureAt;
        bool isRunDay = details.DaysOfWeek.Contains(serviceDate.DayOfWeek);
        return isSameRun && isSameDeparture && isRunDay;
    }

    // A class the trip has no fare for counts as a different fare.
    private static bool HasFares(ScheduleTripRow trip, IReadOnlyList<SeatClassFare> classFares)
    {
        foreach (SeatClassFare classFare in classFares)
        {
            int index = Array.IndexOf(trip.FareClasses, classFare.SeatClass);
            long paise = classFare.Fare * TripRules.PaisePerRupee;
            if (index < 0 || trip.Fares[index] != paise)
            {
                return false;
            }
        }

        return true;
    }

    private static Timetable ToTimetable(ReplacementSource source)
    {
        NewDetails details = source.Replacement.Details;
        var journeyTime = TimeSpan.FromMinutes(source.Stops[^1].OffsetMinutes);
        return new Timetable(details.Departure, details.DaysOfWeek, journeyTime);
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private enum TripFate
    {
        Kept,
        Cancelled,
        Repriced,
    }

    /// <summary>What the edit does to the schedule's trips: the ones it cancels, re-prices and generates.</summary>
    private sealed class RewritePlan
    {
        public required ReplacementSource Source { get; init; }

        public List<Guid> CancelledTripIds { get; } = new List<Guid>();

        public List<Guid> RepricedTripIds { get; } = new List<Guid>();

        /// <summary>Dates that keep a trip, or must not get one back.</summary>
        public HashSet<DateOnly> TakenDates { get; } = new HashSet<DateOnly>();

        public List<PlannedTrip> NewTrips { get; } = new List<PlannedTrip>();
    }

    // Fares are stored in paise; the two arrays pair up by position.
    private sealed class ScheduleTripRow
    {
        public Guid TripId { get; init; }

        public DateTime ServiceDate { get; init; }

        public Guid RouteId { get; init; }

        public Guid BusId { get; init; }

        public DateTime DepartureAt { get; init; }

        public TripStatus Status { get; init; }

        public string[] FareClasses { get; init; } = Array.Empty<string>();

        public long[] Fares { get; init; } = Array.Empty<long>();
    }
}
