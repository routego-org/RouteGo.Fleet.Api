using System.Globalization;

namespace RouteGo.Fleet.Api.Features.Trips.SearchTrips;

/// <summary>Which trips, of every operator, to search for, and the page wanted.</summary>
public sealed class SearchTripsRequest
{
    // Dates and ids arrive as text so a malformed one is refused by the validator under the field
    // name the contract uses; model binding would name the C# property instead.

    /// <summary>Only this operator's trips (a UUID), when given.</summary>
    public string? OperatorId { get; init; }

    /// <summary>Only trips whose route starts at this city (a UUID), when given.</summary>
    public string? SourceCityId { get; init; }

    /// <summary>Only trips whose route ends at this city (a UUID), when given.</summary>
    public string? DestinationCityId { get; init; }

    /// <summary>First service date (IST), YYYY-MM-DD, inclusive; with no dates the search covers today and the next 13 days.</summary>
    public string? From { get; init; }

    /// <summary>Last service date (IST), YYYY-MM-DD, inclusive; not before from, at most 31 days counting both.</summary>
    public string? To { get; init; }

    /// <summary>scheduled, departed or cancelled; every trip when absent.</summary>
    public string? Status { get; init; }

    /// <summary>1-based page number; 1 when absent.</summary>
    public int? Page { get; init; }

    /// <summary>Items per page, 1–200; 20 when absent.</summary>
    public int? PageSize { get; init; }
}

/// <summary>What a seat of one seat class costs on the trip.</summary>
/// <param name="SeatClass">The seat class, spelt as the bus spells it.</param>
/// <param name="Fare">Whole Indian rupees (INR).</param>
public sealed record ClassFare(string SeatClass, int Fare);

/// <summary>A city a trip starts or ends at.</summary>
/// <param name="CityId">The city's identifier.</param>
/// <param name="Name">The city's name.</param>
public sealed record TripCity(Guid CityId, string Name);

/// <summary>One trip of any operator.</summary>
public sealed class Trip
{
    public required Guid TripId { get; init; }

    public required Guid ScheduleId { get; init; }

    public required string OperatorId { get; init; }

    /// <summary>The operator's company name.</summary>
    public required string OperatorName { get; init; }

    public required Guid RouteId { get; init; }

    public required TripCity Source { get; init; }

    public required TripCity Destination { get; init; }

    public required Guid BusId { get; init; }

    /// <summary>The bus's current name.</summary>
    public required string BusName { get; init; }

    /// <summary>The calendar date (IST) on which the trip departs.</summary>
    public required DateOnly ServiceDate { get; init; }

    public required DateTimeOffset DepartureAt { get; init; }

    public required DateTimeOffset ArrivalAt { get; init; }

    /// <summary>Departed when a scheduled trip's departure time has passed.</summary>
    public required TripStatus Status { get; init; }

    /// <summary>One fare for every seat class of the bus, in the bus's seat-class order.</summary>
    public required IReadOnlyList<ClassFare> ClassFares { get; init; }

    /// <summary>The operator's reason, or "Schedule changed" / "Schedule deactivated"; null unless cancelled.</summary>
    public required string? CancelReason { get; init; }

    /// <summary>When the trip was cancelled; null unless cancelled.</summary>
    public required DateTimeOffset? CancelledAt { get; init; }
}

/// <summary>One page of matching trips, sorted by departure, then trip identifier.</summary>
public sealed class SearchTripsResponse
{
    public required IReadOnlyList<Trip> Items { get; init; }

    /// <summary>How many trips match across all pages.</summary>
    public required int TotalCount { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }
}

public sealed class SearchTripsValidator : AbstractValidator<SearchTripsRequest>
{
    /// <summary>The calendar-date format the contract uses.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    public SearchTripsValidator()
    {
        RuleFor(x => x.OperatorId).Must(IsUuid).When(x => x.OperatorId != null)
            .WithMessage("The operator must be a UUID.");
        RuleFor(x => x.SourceCityId).Must(IsUuid).When(x => x.SourceCityId != null)
            .WithMessage("The source city must be a UUID.");
        RuleFor(x => x.DestinationCityId).Must(IsUuid).When(x => x.DestinationCityId != null)
            .WithMessage("The destination city must be a UUID.");
        RuleFor(x => x.From).Must(IsDate).When(x => x.From != null)
            .WithMessage("The from date must be a calendar date, YYYY-MM-DD.");
        RuleFor(x => x.To).Must(IsDate).When(x => x.To != null)
            .WithMessage("The to date must be a calendar date, YYYY-MM-DD.");
        RuleFor(x => x.To).Must(HaveAcceptableRange).When(x => IsDate(x.From) && IsDate(x.To))
            .WithMessage("The to date must not be before the from date, and the range may cover at most 31 days.");
        RuleFor(x => x.Status).Must(IsTripStatus).When(x => x.Status != null)
            .WithMessage("The status must be scheduled, departed or cancelled.");
        RuleFor(x => x.Page).Must(Paging.IsAcceptablePage).WithMessage(Paging.PageMessage);
        RuleFor(x => x.PageSize).Must(Paging.IsAcceptablePageSize).WithMessage(Paging.PageSizeMessage);
    }

    /// <summary>The date a field names; the value has already passed validation.</summary>
    public static DateOnly? ToDate(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return DateOnly.ParseExact(value, DateFormat, CultureInfo.InvariantCulture);
    }

    private static bool HaveAcceptableRange(SearchTripsRequest request, string? to)
    {
        DateOnly from = ToDate(request.From)!.Value;
        DateOnly last = ToDate(to)!.Value;
        return TripRules.IsAcceptableListRange(from, last);
    }

    private static bool IsDate(string? value)
    {
        return DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool IsUuid(string? value)
    {
        return Guid.TryParse(value, out _);
    }

    private static bool IsTripStatus(string? status)
    {
        return WireJson.TryReadEnum<TripStatus>(status, out _);
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Trips")]
[Route("admin")]
public sealed class SearchTripsController : ControllerBase
{
    private readonly SearchTripsHandler _handler;
    private readonly SearchTripsValidator _validator = new SearchTripsValidator();

    public SearchTripsController(IReaderConnectionFactory readerFactory, IClock clock)
    {
        _handler = new SearchTripsHandler(readerFactory, clock);
    }

    /// <summary>Search every operator's trips (Admin only).</summary>
    /// <remarks>
    /// Lists trips of every operator with service dates in the range (IST, inclusive; default today
    /// through today plus 13 days; at most 31 days; with only one end given, 14 days from that end),
    /// sorted by departure, optionally filtered by operator, source city, destination city (the
    /// route's first and last stops) and status. A scheduled trip whose departure has passed is
    /// reported as departed. Read only. Requires role Admin. No match is an empty page, not an error.
    /// </remarks>
    /// <param name="request">The filters, range and the page wanted.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <response code="200">The requested page.</response>
    /// <response code="400">A filter, date, the date range, the page or the page size is invalid (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an admin (FORBIDDEN).</response>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("trips")]
    [ProducesResponseType(typeof(SearchTripsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SearchTripsAsync([FromQuery] SearchTripsRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        SearchTripsResponse response = await _handler.HandleAsync(request, cancellationToken);
        return Ok(response);
    }
}
