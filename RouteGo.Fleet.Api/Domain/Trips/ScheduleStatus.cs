namespace RouteGo.Fleet.Api.Domain.Trips;

/// <summary>Whether a schedule can be edited and extended. Changed only by deactivate and activate.</summary>
public enum ScheduleStatus
{
    Active,
    Inactive,
}
