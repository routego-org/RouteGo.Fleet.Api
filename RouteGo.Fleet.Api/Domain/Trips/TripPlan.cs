namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>
/// Works out which trips a schedule gets — which service dates, when each departs and arrives —
/// and which of them would put the bus on two trips at once. Pure: the caller supplies the clock's
/// now, the dates already taken, and the bus's other trips.
/// </summary>
public static class TripPlan
{
    /// <summary>
    /// One trip for every day of service from today through <paramref name="lastDate"/>, except a
    /// date in <paramref name="takenDates"/> and a date whose departure is not after <paramref name="now"/>.
    /// </summary>
    public static List<PlannedTrip> GenerateTrips(
        DateTimeOffset now, Timetable timetable, DateOnly lastDate, IReadOnlySet<DateOnly> takenDates)
    {
        var trips = new List<PlannedTrip>();
        for (DateOnly serviceDate = TripRules.GetServiceDate(now); serviceDate <= lastDate; serviceDate = serviceDate.AddDays(1))
        {
            DateTimeOffset departureAt = TripRules.GetDepartureAt(serviceDate, timetable.Departure);
            bool isRunDay = timetable.DaysOfWeek.Contains(serviceDate.DayOfWeek);
            bool isOpen = isRunDay && !takenDates.Contains(serviceDate);
            if (isOpen && departureAt > now)
            {
                trips.Add(new PlannedTrip
                {
                    TripId = Guid.NewGuid(),
                    ServiceDate = serviceDate,
                    DepartureAt = departureAt,
                    ArrivalAt = departureAt + timetable.JourneyTime,
                });
            }
        }

        return trips;
    }

    /// <summary>
    /// Every planned trip that would overlap one of the bus's other trips, or an earlier planned
    /// trip, naming the trip it would collide with.
    /// </summary>
    public static List<TripConflict> FindConflicts(IReadOnlyList<PlannedTrip> planned, IReadOnlyList<BusyTrip> busTrips)
    {
        var conflicts = new List<TripConflict>();
        var others = new List<BusyTrip>(busTrips);
        foreach (PlannedTrip trip in planned)
        {
            AddConflicts(trip, others, conflicts);
            others.Add(new BusyTrip(trip.TripId, trip.DepartureAt, trip.ArrivalAt));
        }

        return conflicts;
    }

    private static void AddConflicts(PlannedTrip trip, List<BusyTrip> others, List<TripConflict> conflicts)
    {
        foreach (BusyTrip other in others)
        {
            if (TripRules.IsOverlapping(trip.DepartureAt, trip.ArrivalAt, other.DepartureAt, other.ArrivalAt))
            {
                conflicts.Add(new TripConflict(trip.ServiceDate, other.TripId, other.DepartureAt));
            }
        }
    }
}
