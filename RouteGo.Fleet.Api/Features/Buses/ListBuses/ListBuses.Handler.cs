namespace RouteGo.Fleet.Api.Features.Buses.ListBuses;

public sealed class ListBusesHandler
{
    private const string ProfileExistsSql = """
        SELECT EXISTS (SELECT 1 FROM operator_profiles.operator_profiles WHERE operator_id = @OperatorId)
        """;

    // The cover photo is the uploaded photo in position 1; only uploaded photos have a position, so
    // a pending upload slot never becomes a cover. The bus id breaks ties between buses whose names
    // differ only in case, so paging never repeats or skips one.
    private const string SelectPageSql = """
        SELECT count(*)::int
        FROM buses.buses
        WHERE operator_id = @OperatorId
          AND (CAST(@Status AS text) IS NULL OR status = @Status);

        SELECT b.bus_id, b.registration_number, b.name, b.bus_type, b.is_ac, b.status,
               (SELECT count(*)::int FROM buses.bus_seats s WHERE s.bus_id = b.bus_id) AS total_seats,
               cover.file_location AS cover_file_location
        FROM buses.buses b
        LEFT JOIN buses.bus_photos cover ON cover.bus_id = b.bus_id AND cover.position = 1
        WHERE b.operator_id = @OperatorId
          AND (CAST(@Status AS text) IS NULL OR b.status = @Status)
        ORDER BY lower(b.name), b.bus_id
        OFFSET @Offset LIMIT @Limit;
        """;

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly PhotoAddresses _photoAddresses;

    public ListBusesHandler(IReaderConnectionFactory readerFactory, PhotoAddresses photoAddresses)
    {
        _readerFactory = readerFactory;
        _photoAddresses = photoAddresses;
    }

    /// <summary>Lists one page of the operator's buses, provided the operator has its Fleet profile.</summary>
    public async Task<ListBusesResult> HandleAsync(
        ListBusesRequest request, string operatorId, CancellationToken cancellationToken)
    {
        var filter = new BusFilter(operatorId, ToStatusColumn(request.Status), Paging.Normalize(request.Page, request.PageSize));
        return await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, filter, cancellationToken), cancellationToken);
    }

    private async Task<ListBusesResult> ReadAsync(
        DbConnection connection, BusFilter filter, CancellationToken cancellationToken)
    {
        bool hasProfile = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            ProfileExistsSql, new { filter.OperatorId }, cancellationToken: cancellationToken));
        if (!hasProfile)
        {
            return new ListBusesResult(ListBusesOutcome.OperatorProfileNotReady);
        }

        var parameters = new
        {
            filter.OperatorId,
            filter.Status,
            filter.Page.Offset,
            Limit = filter.Page.PageSize,
        };
        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(new CommandDefinition(
            SelectPageSql, parameters, cancellationToken: cancellationToken));
        int totalCount = await results.ReadSingleAsync<int>();
        IEnumerable<BusRow> buses = await results.ReadAsync<BusRow>();
        ListBusesResponse response = ToResponse(filter.Page, totalCount, buses, _photoAddresses);
        return new ListBusesResult(ListBusesOutcome.Listed, response);
    }

    // The validator has already refused any spelling that is not a bus status.
    private static string? ToStatusColumn(string? status)
    {
        if (!WireJson.TryReadEnum(status, out BusStatus value))
        {
            return null;
        }

        return value.ToString();
    }

    private static ListBusesResponse ToResponse(
        PageRequest page, int totalCount, IEnumerable<BusRow> buses, PhotoAddresses photoAddresses)
    {
        var items = new List<BusSummary>();
        foreach (BusRow bus in buses)
        {
            items.Add(ToSummary(bus, photoAddresses));
        }

        return new ListBusesResponse { Items = items, TotalCount = totalCount, Page = page.Page, PageSize = page.PageSize };
    }

    private static BusSummary ToSummary(BusRow bus, PhotoAddresses photoAddresses)
    {
        Uri? coverPhotoUrl = null;
        if (bus.CoverFileLocation != null)
        {
            coverPhotoUrl = photoAddresses.GetAddress(bus.CoverFileLocation);
        }

        return new BusSummary
        {
            BusId = bus.BusId,
            RegistrationNumber = bus.RegistrationNumber,
            Name = bus.Name,
            BusType = bus.BusType,
            IsAc = bus.IsAc,
            TotalSeats = bus.TotalSeats,
            Status = bus.Status,
            CoverPhotoUrl = coverPhotoUrl,
        };
    }

    /// <param name="OperatorId">The calling operator; only its buses are listed.</param>
    /// <param name="Status">The stored status to keep, or null for every bus.</param>
    /// <param name="Page">The page wanted.</param>
    private sealed record BusFilter(string OperatorId, string? Status, PageRequest Page);

    private sealed class BusRow
    {
        public Guid BusId { get; init; }

        public string RegistrationNumber { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public BusType BusType { get; init; }

        public bool IsAc { get; init; }

        public BusStatus Status { get; init; }

        public int TotalSeats { get; init; }

        public string? CoverFileLocation { get; init; }
    }
}
