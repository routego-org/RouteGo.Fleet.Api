namespace RouteGo.Fleet.Api.Features.Routes.ListCities;

/// <summary>A place a route can start at, stop at or end at.</summary>
/// <param name="CityId">The city's identifier, the same in every environment.</param>
/// <param name="Name">The city's name, unique within its state.</param>
/// <param name="State">The Indian state or union territory the city is in.</param>
public sealed record City(Guid CityId, string Name, string State);

/// <summary>Every city, sorted by name. The list is small, so it is never paged.</summary>
/// <param name="Items">The cities.</param>
public sealed record ListCitiesResponse(IReadOnlyList<City> Items);

public sealed class ListCitiesHandler
{
    // State breaks ties so the order is stable should two states ever share a city name.
    private const string SelectCitiesSql = """
        SELECT city_id, name, state
        FROM routes.cities
        ORDER BY name, state
        """;

    private readonly IReaderConnectionFactory _readerFactory;

    public ListCitiesHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    public async Task<ListCitiesResponse> HandleAsync(CancellationToken cancellationToken)
    {
        var command = new CommandDefinition(SelectCitiesSql, cancellationToken: cancellationToken);
        IEnumerable<City> cities = await _readerFactory.ExecuteReadAsync(
            connection => connection.QueryAsync<City>(command), cancellationToken);
        return new ListCitiesResponse(cities.ToList());
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Routes")]
[Route("cities")]
public sealed class ListCitiesController : ControllerBase
{
    private readonly ListCitiesHandler _handler;

    public ListCitiesController(IReaderConnectionFactory readerFactory)
    {
        _handler = new ListCitiesHandler(readerFactory);
    }

    /// <summary>List the cities routes can use (any signed-in account).</summary>
    /// <remarks>
    /// Returns the whole city list, sorted by name, without paging. Any signed-in account may read
    /// it, whatever its role; an operator does not need its Fleet profile.
    /// </remarks>
    /// <response code="200">Every city.</response>
    /// <response code="401">Missing, expired or invalid access token (RFC 7807 problem+json, code UNAUTHORIZED).</response>
    [Authorize]
    [HttpGet]
    [ProducesResponseType(typeof(ListCitiesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListCitiesAsync(CancellationToken cancellationToken)
    {
        ListCitiesResponse response = await _handler.HandleAsync(cancellationToken);
        return Ok(response);
    }
}
