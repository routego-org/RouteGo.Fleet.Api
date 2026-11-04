namespace RouteGo.Fleet.Api.Common.Problems;

/// <summary>
/// Projection of the product's problem-type catalog (contracts/problem-types.md in the
/// product-specs repo). Codes are never minted here — a new code starts life in the catalog
/// and lands here as a constant carrying the same code and the same type URI, so this class
/// can be diffed against the catalog rather than trusted to match it.
/// <para>
/// Seeded with every baseline code this codebase itself emits. 401/403 are written by the
/// authentication handler (Common/Security/AuthenticationProblems), 429 would be the rate
/// limiter's, and 400 validation problems are identified by their <c>errors</c> member rather
/// than a code, by contract. Append one constant per product code as capabilities emit them
/// (references/host-components.md).
/// </para>
/// </summary>
public static class ProblemTypes
{
    // The catalog joins the base and the kebab-case name with ':' — a URN, not a path.
    private const string ProblemsBase = "urn:routego:fleet:problems";

    /// <summary>400 — the request body or parameters failed shape validation; carries <c>errors</c>.</summary>
    public static readonly ProblemType ValidationFailed =
        new("VALIDATION_FAILED", $"{ProblemsBase}:validation-failed");

    /// <summary>401 — the access token is missing, expired or invalid.</summary>
    public static readonly ProblemType Unauthorized = new("UNAUTHORIZED", $"{ProblemsBase}:unauthorized");

    /// <summary>403 — the caller is signed in but its role may not perform the operation.</summary>
    public static readonly ProblemType Forbidden = new("FORBIDDEN", $"{ProblemsBase}:forbidden");

    /// <summary>404 — the resource does not exist.</summary>
    public static readonly ProblemType NotFound = new("NOT_FOUND", $"{ProblemsBase}:not-found");

    /// <summary>409 — state changed since read, or the request duplicates existing state.</summary>
    public static readonly ProblemType Conflict = new("CONFLICT", $"{ProblemsBase}:conflict");

    /// <summary>422 — a business rule refused the request and no specific code exists yet.</summary>
    public static readonly ProblemType BusinessRuleViolated =
        new("BUSINESS_RULE_VIOLATED", $"{ProblemsBase}:business-rule-violated");

    /// <summary>502 — a required dependency is down or returned an unusable response.</summary>
    public static readonly ProblemType UpstreamUnavailable =
        new("UPSTREAM_UNAVAILABLE", $"{ProblemsBase}:upstream-unavailable");

    /// <summary>504 — a required dependency did not respond in time and no data could be served.</summary>
    public static readonly ProblemType UpstreamTimeout =
        new("UPSTREAM_TIMEOUT", $"{ProblemsBase}:upstream-timeout");

    /// <summary>403 — an operator called Fleet before Fleet created its operator profile.</summary>
    public static readonly ProblemType OperatorProfileNotReady =
        new("OPERATOR_PROFILE_NOT_READY", $"{ProblemsBase}:operator-profile-not-ready");

    /// <summary>409 — a bus with this registration number already exists in RouteGo.</summary>
    public static readonly ProblemType BusRegistrationAlreadyExists =
        new("BUS_REGISTRATION_ALREADY_EXISTS", $"{ProblemsBase}:bus-registration-already-exists");

    /// <summary>409 — the bus has future scheduled trips; carries <c>futureTripCount</c> and <c>nextDepartureAt</c>.</summary>
    public static readonly ProblemType BusInUse = new("BUS_IN_USE", $"{ProblemsBase}:bus-in-use");

    /// <summary>422 — the bus already has its maximum of photos and open upload slots; carries <c>maxPhotos</c>.</summary>
    public static readonly ProblemType PhotoLimitReached =
        new("PHOTO_LIMIT_REACHED", $"{ProblemsBase}:photo-limit-reached");

    /// <summary>422 — the upload slot expired before it was confirmed; carries <c>expiredAt</c>.</summary>
    public static readonly ProblemType PhotoUploadExpired =
        new("PHOTO_UPLOAD_EXPIRED", $"{ProblemsBase}:photo-upload-expired");

    /// <summary>422 — no file has reached the media store for the upload slot yet.</summary>
    public static readonly ProblemType PhotoNotUploaded =
        new("PHOTO_NOT_UPLOADED", $"{ProblemsBase}:photo-not-uploaded");

    /// <summary>422 — the stored file is too large, not the declared format, or not a JPEG or PNG; carries <c>reason</c>.</summary>
    public static readonly ProblemType PhotoInvalid = new("PHOTO_INVALID", $"{ProblemsBase}:photo-invalid");

    /// <summary>422 — a route names a city that is not in the city list; carries <c>cityId</c>.</summary>
    public static readonly ProblemType UnknownCity = new("UNKNOWN_CITY", $"{ProblemsBase}:unknown-city");

    /// <summary>422 — a schedule names a bus that does not exist or is not the operator's; carries <c>busId</c>.</summary>
    public static readonly ProblemType UnknownBus = new("UNKNOWN_BUS", $"{ProblemsBase}:unknown-bus");

    /// <summary>422 — a schedule names a route that does not exist or is not the operator's; carries <c>routeId</c>.</summary>
    public static readonly ProblemType UnknownRoute = new("UNKNOWN_ROUTE", $"{ProblemsBase}:unknown-route");

    /// <summary>422 — a schedule is created, edited or extended with an inactive bus; carries <c>busId</c>.</summary>
    public static readonly ProblemType BusInactive = new("BUS_INACTIVE", $"{ProblemsBase}:bus-inactive");

    /// <summary>422 — a schedule is created on, or moved to, an inactive route; carries <c>routeId</c>.</summary>
    public static readonly ProblemType RouteInactive = new("ROUTE_INACTIVE", $"{ProblemsBase}:route-inactive");

    /// <summary>422 — the fares do not name exactly the bus's seat classes; carries <c>missingClasses</c> and <c>unknownClasses</c>.</summary>
    public static readonly ProblemType ClassFaresMismatch =
        new("CLASS_FARES_MISMATCH", $"{ProblemsBase}:class-fares-mismatch");

    /// <summary>409 — the request would put a bus on overlapping trips; carries <c>conflicts</c>.</summary>
    public static readonly ProblemType BusTripOverlap = new("BUS_TRIP_OVERLAP", $"{ProblemsBase}:bus-trip-overlap");

    /// <summary>409 — an inactive schedule cannot be edited or extended.</summary>
    public static readonly ProblemType ScheduleInactive = new("SCHEDULE_INACTIVE", $"{ProblemsBase}:schedule-inactive");

    /// <summary>422 — an extension's end date is before today or past the horizon; carries <c>latestAllowedDate</c>.</summary>
    public static readonly ProblemType TripEndDateOutOfRange =
        new("TRIP_END_DATE_OUT_OF_RANGE", $"{ProblemsBase}:trip-end-date-out-of-range");

    /// <summary>409 — the fares of, or a cancellation of, a trip that is cancelled; carries <c>currentStatus</c>.</summary>
    public static readonly ProblemType TripNotScheduled = new("TRIP_NOT_SCHEDULED", $"{ProblemsBase}:trip-not-scheduled");

    /// <summary>409 — the fares of, or a cancellation of, a trip whose departure time has passed; carries <c>departedAt</c>.</summary>
    public static readonly ProblemType TripDeparted = new("TRIP_DEPARTED", $"{ProblemsBase}:trip-departed");

    /// <summary>422 — a trip's fares changed less than 6 hours before departure; carries <c>cutoffAt</c>.</summary>
    public static readonly ProblemType FareChangeCutoffPassed =
        new("FARE_CHANGE_CUTOFF_PASSED", $"{ProblemsBase}:fare-change-cutoff-passed");

    /// <summary>
    /// Worker alert, no HTTP status — an OperatorApproved event could not be turned into an operator
    /// profile; raised with the event ID and the reason (<c>invalidEventData</c> or <c>repeatedFailure</c>).
    /// </summary>
    public static readonly ProblemType OperatorProfileNotCreated =
        new("OPERATOR_PROFILE_NOT_CREATED", $"{ProblemsBase}:operator-profile-not-created");

    /// <summary>
    /// Worker alert, no HTTP status — a Fleet event has stayed undelivered for more than 5 minutes;
    /// raised with the event ID, event type and recorded time.
    /// </summary>
    public static readonly ProblemType EventPublishDelayed =
        new("EVENT_PUBLISH_DELAYED", $"{ProblemsBase}:event-publish-delayed");

    /// <summary>500 — unhandled server fault. Emitted by UnhandledExceptionHandler, never by a slice.</summary>
    public static readonly ProblemType InternalError =
        new("INTERNAL_ERROR", $"{ProblemsBase}:internal-error");
}
