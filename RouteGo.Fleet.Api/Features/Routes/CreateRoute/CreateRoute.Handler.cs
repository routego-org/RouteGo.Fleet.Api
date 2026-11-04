namespace RouteGo.Fleet.Api.Features.Routes.CreateRoute;

public sealed class CreateRouteHandler
{
    private const string AuditAction = "CreateRoute";

    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    private const string SelectCitiesSql = """
        SELECT city_id, name, state
        FROM routes.cities
        WHERE city_id = ANY(@CityIds)
        """;

    private const string InsertRouteSql = """
        INSERT INTO routes.routes (operator_id, distance_km, status)
        VALUES (@OperatorId, @DistanceKm, @Status)
        RETURNING route_id, created_at
        """;

    private const string InsertStopsSql = """
        INSERT INTO routes.route_stops (route_id, sequence, city_id, offset_minutes)
        SELECT @RouteId, s.sequence, s.city_id, s.offset_minutes
        FROM unnest(@Sequences, @CityIds, @Offsets) AS s(sequence, city_id, offset_minutes)
        """;

    private readonly IWriterConnectionFactory _writerFactory;

    public CreateRouteHandler(IWriterConnectionFactory writerFactory)
    {
        _writerFactory = writerFactory;
    }

    /// <summary>
    /// Creates the active route and its stops in one transaction, provided the operator has its
    /// Fleet profile and every stop is a city from the city list.
    /// </summary>
    public async Task<CreateRouteResult> HandleAsync(CreateRouteRequest request, string operatorId, CancellationToken cancellationToken)
    {
        Guid[] cityIds = request.Stops!.Select(stop => stop.CityId!.Value).ToArray();

        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        bool hasProfile = await writer.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { OperatorId = operatorId }, writer.Transaction, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new CreateRouteResult { Outcome = CreateRouteOutcome.OperatorProfileNotReady };
        }

        Dictionary<Guid, City> cities = await ListCitiesAsync(writer, cityIds, cancellationToken);
        Guid? unknownCityId = FirstUnknownCity(cityIds, cities);
        if (unknownCityId != null)
        {
            return new CreateRouteResult { Outcome = CreateRouteOutcome.UnknownCity, UnknownCityId = unknownCityId };
        }

        InsertedRoute inserted = await InsertRouteAsync(writer, operatorId, request, cityIds, cancellationToken);
        await writer.CommitAsync(cancellationToken);
        return new CreateRouteResult { Outcome = CreateRouteOutcome.Created, Response = ToResponse(request, operatorId, cities, inserted) };
    }

    private static async Task<Dictionary<Guid, City>> ListCitiesAsync(
        IWriterConnection writer, Guid[] cityIds, CancellationToken cancellationToken)
    {
        IEnumerable<City> cities = await writer.Connection.QueryAsync<City>(new CommandDefinition(
            SelectCitiesSql, new { CityIds = cityIds }, writer.Transaction, cancellationToken: cancellationToken));
        return cities.ToDictionary(city => city.CityId);
    }

    // The first in stop order, so the operator is pointed at the earliest stop to fix.
    private static Guid? FirstUnknownCity(Guid[] cityIds, Dictionary<Guid, City> cities)
    {
        foreach (Guid cityId in cityIds)
        {
            if (!cities.ContainsKey(cityId))
            {
                return cityId;
            }
        }

        return null;
    }

    private static async Task<InsertedRoute> InsertRouteAsync(
        IWriterConnection writer, string operatorId, CreateRouteRequest request, Guid[] cityIds, CancellationToken cancellationToken)
    {
        var route = new { OperatorId = operatorId, request.DistanceKm, Status = nameof(RouteStatus.Active) };
        InsertedRoute inserted = await writer.Connection.QuerySingleAsync<InsertedRoute>(new CommandDefinition(
            InsertRouteSql, route, writer.Transaction, cancellationToken: cancellationToken));

        var stops = new
        {
            inserted.RouteId,
            Sequences = Enumerable.Range(1, cityIds.Length).ToArray(),
            CityIds = cityIds,
            Offsets = request.Stops!.Select(stop => stop.OffsetMinutes!.Value).ToArray(),
        };
        await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertStopsSql, stops, writer.Transaction, cancellationToken: cancellationToken));
        return inserted;
    }

    private static CreateRouteResponse ToResponse(
        CreateRouteRequest request, string operatorId, Dictionary<Guid, City> cities, InsertedRoute inserted)
    {
        var stops = new List<RouteStop>();
        foreach (RouteStopInput stop in request.Stops!)
        {
            stops.Add(new RouteStop(stops.Count + 1, cities[stop.CityId!.Value], stop.OffsetMinutes!.Value));
        }

        return new CreateRouteResponse
        {
            RouteId = inserted.RouteId,
            OperatorId = operatorId,
            Source = stops[0].City,
            Destination = stops[^1].City,
            Stops = stops,
            DistanceKm = request.DistanceKm!.Value,
            JourneyMinutes = stops[^1].OffsetMinutes,
            Status = RouteStatus.Active,
            CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(inserted.CreatedAt, DateTimeKind.Utc)),
            DeactivatedAt = null,
        };
    }

    private sealed record InsertedRoute(Guid RouteId, DateTime CreatedAt);
}
