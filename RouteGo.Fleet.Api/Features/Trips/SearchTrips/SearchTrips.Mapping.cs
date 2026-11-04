namespace RouteGo.Fleet.Api.Features.Trips.SearchTrips;

// Turning the stored rows into the page: fares in whole rupees, departed derived from the instant.
public sealed partial class SearchTripsHandler
{
    private static SearchTripsResponse ToResponse(TripFilter filter, int totalCount, List<TripRow> trips, List<FareRow> fares)
    {
        var items = new List<Trip>();
        foreach (TripRow trip in trips)
        {
            var tripFares = fares.Where(fare => fare.TripId == trip.TripId).ToList();
            items.Add(ToTrip(trip, tripFares, filter.Now));
        }

        return new SearchTripsResponse { Items = items, TotalCount = totalCount, Page = filter.Page.Page, PageSize = filter.Page.PageSize };
    }

    private static Trip ToTrip(TripRow trip, List<FareRow> fares, DateTimeOffset now)
    {
        var classFares = new List<ClassFare>();
        foreach (FareRow fare in fares)
        {
            classFares.Add(new ClassFare(fare.SeatClass, (int)(fare.Fare / TripRules.PaisePerRupee)));
        }

        DateTimeOffset departureAt = ToUtc(trip.DepartureAt);
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
            DepartureAt = departureAt,
            ArrivalAt = ToUtc(trip.ArrivalAt),
            Status = GetStatus(trip.Status, departureAt, now),
            ClassFares = classFares,
            CancelReason = trip.CancelReason,
            CancelledAt = ToUtc(trip.CancelledAt),
        };
    }

    private static TripStatus GetStatus(TripStatus stored, DateTimeOffset departureAt, DateTimeOffset now)
    {
        bool hasDeparted = stored == TripStatus.Scheduled && departureAt <= now;
        if (hasDeparted)
        {
            return TripStatus.Departed;
        }

        return stored;
    }

    private static DateTimeOffset ToUtc(DateTime value)
    {
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static DateTimeOffset? ToUtc(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        return ToUtc(value.Value);
    }

    private sealed class TripRow
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

        /// <summary>Scheduled or Cancelled, as stored.</summary>
        public TripStatus Status { get; init; }

        public string? CancelReason { get; init; }

        public DateTime? CancelledAt { get; init; }
    }

    // Fares are stored in paise and shown in whole rupees.
    private sealed class FareRow
    {
        public Guid TripId { get; init; }

        public string SeatClass { get; init; } = string.Empty;

        public long Fare { get; init; }
    }
}
