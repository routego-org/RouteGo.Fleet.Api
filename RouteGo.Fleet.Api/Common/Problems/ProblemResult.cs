namespace RouteGo.Fleet.Api.Common.Problems;

/// <summary>
/// An RFC 7807 problem response reporting one cataloged problem type. Every business refusal
/// in every slice is built here, so the same refusal always carries the same type URI, title,
/// status, and machine-readable code — and the catalog row stays inspectable, which is what
/// lets the codebase be checked against the product's problem-type catalog rather than
/// trusted to match it.
/// <para>
/// Extending this: one factory method per row in the product's problem-type catalog, and
/// never a method for a code the catalog does not define. The <c>detail</c> sentence is
/// always a parameter — it is the one part the catalog leaves to the operation — and no
/// slice-specific logic belongs here. The constructor is private so a problem response can
/// only ever be created through a cataloged factory.
/// </para>
/// </summary>
public sealed class ProblemResult : ObjectResult
{
    private readonly ProblemDetails _problemDetails;

    private ProblemResult(ProblemDetails problemDetails, ProblemType problemType)
        : base(problemDetails)
    {
        _problemDetails = problemDetails;
        ProblemType = problemType;
        StatusCode = problemDetails.Status;
    }

    /// <summary>The catalog row this response reports. Clients switch on its code.</summary>
    public ProblemType ProblemType { get; }

    /// <summary>403 — the operator's Fleet profile has not been created yet.</summary>
    public static ProblemResult OperatorProfileNotReady(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.OperatorProfileNotReady,
            "Your operator account is being set up. Please try again in a minute.",
            StatusCodes.Status403Forbidden, detail);
    }

    /// <summary>409 — the registration number belongs to a bus already in RouteGo.</summary>
    public static ProblemResult BusRegistrationAlreadyExists(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.BusRegistrationAlreadyExists,
            "This registration number is already registered with RouteGo.",
            StatusCodes.Status409Conflict, detail);
    }

    /// <summary>409 — the bus has future scheduled trips, so it is locked; the client says when the next one departs.</summary>
    public static ProblemResult BusInUse(
        ControllerBase controller, string detail, int futureTripCount, DateTimeOffset nextDepartureAt)
    {
        ProblemResult result = Build(controller, ProblemTypes.BusInUse, "This bus has future trips.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("futureTripCount", futureTripCount);
        result.AddExtension("nextDepartureAt", nextDepartureAt);
        return result;
    }

    /// <summary>422 — the bus already has its maximum of photos, counting open upload slots.</summary>
    public static ProblemResult PhotoLimitReached(ControllerBase controller, string detail, int maxPhotos)
    {
        ProblemResult result = Build(controller, ProblemTypes.PhotoLimitReached,
            $"A bus can have at most {maxPhotos} photos.", StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("maxPhotos", maxPhotos);
        return result;
    }

    /// <summary>422 — the upload slot expired before confirmation; the operator uploads the photo again.</summary>
    public static ProblemResult PhotoUploadExpired(ControllerBase controller, string detail, DateTimeOffset expiredAt)
    {
        ProblemResult result = Build(controller, ProblemTypes.PhotoUploadExpired,
            "The upload took too long. Please upload the photo again.", StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("expiredAt", expiredAt);
        return result;
    }

    /// <summary>422 — the photo file has not reached the media store yet; the slot stays usable.</summary>
    public static ProblemResult PhotoNotUploaded(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.PhotoNotUploaded, "The photo file has not arrived yet.",
            StatusCodes.Status422UnprocessableEntity, detail);
    }

    /// <summary>422 — the stored file cannot be used; <paramref name="reason"/> is tooLarge, formatMismatch or unsupportedFormat.</summary>
    public static ProblemResult PhotoInvalid(ControllerBase controller, string detail, string reason)
    {
        ProblemResult result = Build(controller, ProblemTypes.PhotoInvalid, "This photo cannot be used.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("reason", reason);
        return result;
    }

    /// <summary>422 — a route names a city that is not in the city list; the client reloads the list.</summary>
    public static ProblemResult UnknownCity(ControllerBase controller, string detail, Guid cityId)
    {
        ProblemResult result = Build(controller, ProblemTypes.UnknownCity, "This city is not available.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("cityId", cityId);
        return result;
    }

    /// <summary>422 — the bus does not exist or is not the operator's; the client reloads the bus list.</summary>
    public static ProblemResult UnknownBus(ControllerBase controller, string detail, Guid busId)
    {
        ProblemResult result = Build(controller, ProblemTypes.UnknownBus, "This bus was not found among your buses.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("busId", busId);
        return result;
    }

    /// <summary>422 — the route does not exist or is not the operator's; the client reloads the route list.</summary>
    public static ProblemResult UnknownRoute(ControllerBase controller, string detail, Guid routeId)
    {
        ProblemResult result = Build(controller, ProblemTypes.UnknownRoute, "This route was not found among your routes.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("routeId", routeId);
        return result;
    }

    /// <summary>422 — the bus is inactive; the client offers to activate it or choose another.</summary>
    public static ProblemResult BusInactive(ControllerBase controller, string detail, Guid busId)
    {
        ProblemResult result = Build(controller, ProblemTypes.BusInactive, "This bus is inactive.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("busId", busId);
        return result;
    }

    /// <summary>422 — the route is inactive; the client asks for another route.</summary>
    public static ProblemResult RouteInactive(ControllerBase controller, string detail, Guid routeId)
    {
        ProblemResult result = Build(controller, ProblemTypes.RouteInactive, "This route is inactive.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("routeId", routeId);
        return result;
    }

    /// <summary>422 — the fares do not name exactly the bus's seat classes; the client highlights both lists.</summary>
    public static ProblemResult ClassFaresMismatch(
        ControllerBase controller, string detail, IReadOnlyList<string> missingClasses, IReadOnlyList<string> unknownClasses)
    {
        ProblemResult result = Build(controller, ProblemTypes.ClassFaresMismatch,
            "Give one fare for every seat class of the bus, and no others.", StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("missingClasses", missingClasses);
        result.AddExtension("unknownClasses", unknownClasses);
        return result;
    }

    /// <summary>409 — the bus would be on two trips at once; the client lists every conflicting date and trip.</summary>
    public static ProblemResult BusTripOverlap(ControllerBase controller, string detail, IReadOnlyList<TripConflict> conflicts)
    {
        ProblemResult result = Build(controller, ProblemTypes.BusTripOverlap, "The bus is already on another trip at this time.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("conflicts", conflicts);
        return result;
    }

    /// <summary>409 — the schedule is inactive; the client offers to activate it.</summary>
    public static ProblemResult ScheduleInactive(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.ScheduleInactive, "This schedule is inactive.",
            StatusCodes.Status409Conflict, detail);
    }

    /// <summary>422 — the extension's end date is out of range; the client shows the latest allowed date.</summary>
    public static ProblemResult TripEndDateOutOfRange(ControllerBase controller, string detail, DateOnly latestAllowedDate)
    {
        ProblemResult result = Build(controller, ProblemTypes.TripEndDateOutOfRange,
            "The end date must be between today and the latest allowed date.", StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("latestAllowedDate", latestAllowedDate);
        return result;
    }

    /// <summary>409 — the trip is cancelled; the client reloads it and shows its current status.</summary>
    public static ProblemResult TripNotScheduled(ControllerBase controller, string detail, TripStatus currentStatus)
    {
        ProblemResult result = Build(controller, ProblemTypes.TripNotScheduled, "This trip is not scheduled.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("currentStatus", currentStatus);
        return result;
    }

    /// <summary>409 — the trip's departure time has passed; the client says it has already departed.</summary>
    public static ProblemResult TripDeparted(ControllerBase controller, string detail, DateTimeOffset departedAt)
    {
        ProblemResult result = Build(controller, ProblemTypes.TripDeparted, "This trip has already departed.",
            StatusCodes.Status409Conflict, detail);
        result.AddExtension("departedAt", departedAt);
        return result;
    }

    /// <summary>422 — the trip's fares are frozen; the client shows when they froze.</summary>
    public static ProblemResult FareChangeCutoffPassed(ControllerBase controller, string detail, DateTimeOffset cutoffAt)
    {
        ProblemResult result = Build(controller, ProblemTypes.FareChangeCutoffPassed, "Fares can no longer change for this trip.",
            StatusCodes.Status422UnprocessableEntity, detail);
        result.AddExtension("cutoffAt", cutoffAt);
        return result;
    }

    /// <summary>404 — the resource does not exist.</summary>
    public static ProblemResult NotFound(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.NotFound, "The requested resource does not exist.",
            StatusCodes.Status404NotFound, detail);
    }

    /// <summary>409 — the request conflicts with existing state.</summary>
    public static ProblemResult Conflict(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.Conflict, "The request conflicts with existing state.",
            StatusCodes.Status409Conflict, detail);
    }

    /// <summary>422 — a business rule refused an otherwise well-formed request.</summary>
    public static ProblemResult BusinessRuleViolated(ControllerBase controller, string detail)
    {
        return Build(controller, ProblemTypes.BusinessRuleViolated, "A business rule refused the request.",
            StatusCodes.Status422UnprocessableEntity, detail);
    }

    /// <summary>
    /// 502 — a required dependency is down or returned an unusable response, leaving no usable
    /// data. A fan-out that got data from at least one source is a SUCCESS: return 200 with the
    /// contract's partial-success fields instead of a problem.
    /// </summary>
    public static ProblemResult UpstreamUnavailable(ControllerBase controller, string detail, bool retryable)
    {
        ProblemResult result = Build(controller, ProblemTypes.UpstreamUnavailable,
            "A required upstream service is unavailable.",
            StatusCodes.Status502BadGateway, detail);
        result.AddExtension("retryable", retryable);
        return result;
    }

    /// <summary>
    /// 504 — a required dependency did not respond in time and no data could be served. Pass
    /// retryAfterSeconds when the delay is known; the catalog documents it as a member clients
    /// act on. Same partial-success rule as UpstreamUnavailable.
    /// </summary>
    public static ProblemResult UpstreamTimeout(
        ControllerBase controller, string detail, bool retryable, int? retryAfterSeconds)
    {
        ProblemResult result = Build(controller, ProblemTypes.UpstreamTimeout,
            "A required upstream service did not respond in time.",
            StatusCodes.Status504GatewayTimeout, detail);
        result.AddExtension("retryable", retryable);
        if (retryAfterSeconds.HasValue)
        {
            result.AddExtension("retryAfterSeconds", retryAfterSeconds.Value);
        }

        return result;
    }

    /// <summary>
    /// Adds one extension member. Every member added here must appear in the catalog row's
    /// Extensions column — a member no consumer was told about is a member no consumer reads.
    /// </summary>
    private void AddExtension(string name, object value)
    {
        _problemDetails.Extensions[name] = value;
    }

    private static ProblemResult Build(
        ControllerBase controller, ProblemType problemType, string title, int statusCode, string detail)
    {
        // Built through the controller's ProblemDetailsFactory rather than by hand, because
        // that factory is what applies the host's customization — the traceId correlating this
        // response with server-side telemetry. A hand-constructed ProblemDetails loses it
        // silently, and the loss only shows up when someone tries to trace a reported error.
        ProblemDetails problemDetails = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext,
            statusCode: statusCode,
            title: title,
            type: problemType.Type,
            detail: detail);
        problemDetails.Extensions["code"] = problemType.Code;
        return new ProblemResult(problemDetails, problemType);
    }
}
