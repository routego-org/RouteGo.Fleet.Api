namespace RouteGo.Fleet.Api.Features.Buses.GetPublicBus;

/// <summary>What anyone may see of a bus: no registration number, operator email, seat layout or pending upload.</summary>
public sealed class GetPublicBusResponse
{
    public required Guid BusId { get; init; }

    public required string Name { get; init; }

    public required BusType BusType { get; init; }

    public required bool IsAc { get; init; }

    public required IReadOnlyList<Amenity> Amenities { get; init; }

    /// <summary>The seat class names in the order the operator gave them.</summary>
    public required IReadOnlyList<string> SeatClasses { get; init; }

    public required int TotalSeats { get; init; }

    /// <summary>The operator's company name.</summary>
    public required string OperatorName { get; init; }

    /// <summary>0.0 to 5.0.</summary>
    public required decimal OperatorRating { get; init; }

    /// <summary>Uploaded photos in order; the first is the cover photo.</summary>
    public required IReadOnlyList<PublicBusPhoto> Photos { get; init; }
}

/// <summary>One uploaded photo of the bus.</summary>
/// <param name="PhotoId">The photo's identifier.</param>
/// <param name="Position">Its place in the bus's photo order, 1 for the cover photo.</param>
/// <param name="Url">Its CDN address.</param>
public sealed record PublicBusPhoto(Guid PhotoId, int Position, Uri Url);

public sealed class GetPublicBusHandler
{
    // Every bus belongs to an operator with a profile (the foreign key guarantees it), so the join
    // never drops a bus.
    private const string SelectBusSql = """
        SELECT b.bus_id, b.name, b.bus_type, b.is_ac, b.amenities,
               o.company_name AS operator_name, o.rating AS operator_rating,
               (SELECT count(*)::int FROM buses.bus_seats s WHERE s.bus_id = b.bus_id) AS total_seats
        FROM buses.buses b
        JOIN operator_profiles.operator_profiles o ON o.operator_id = b.operator_id
        WHERE b.bus_id = @BusId
        """;

    // Only uploaded photos are public; a pending upload slot is not shown.
    private const string SelectPartsSql = """
        SELECT name
        FROM buses.seat_classes
        WHERE bus_id = @BusId
        ORDER BY display_order;

        SELECT photo_id, position::int AS position, file_location
        FROM buses.bus_photos
        WHERE bus_id = @BusId AND status = 'Uploaded'
        ORDER BY position;
        """;

    private readonly IReaderConnectionFactory _readerFactory;
    private readonly PhotoAddresses _photoAddresses;

    public GetPublicBusHandler(IReaderConnectionFactory readerFactory, PhotoAddresses photoAddresses)
    {
        _readerFactory = readerFactory;
        _photoAddresses = photoAddresses;
    }

    /// <summary>Reads any bus's public details, active or inactive; null when there is no such bus.</summary>
    public async Task<GetPublicBusResponse?> HandleAsync(Guid busId, CancellationToken cancellationToken)
    {
        return await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, busId, cancellationToken), cancellationToken);
    }

    private async Task<GetPublicBusResponse?> ReadAsync(DbConnection connection, Guid busId, CancellationToken cancellationToken)
    {
        BusRow? bus = await connection.QuerySingleOrDefaultAsync<BusRow>(new CommandDefinition(
            SelectBusSql, new { BusId = busId }, cancellationToken: cancellationToken));
        if (bus is null)
        {
            return null;
        }

        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(new CommandDefinition(
            SelectPartsSql, new { BusId = busId }, cancellationToken: cancellationToken));
        IEnumerable<string> seatClasses = await results.ReadAsync<string>();
        IEnumerable<PhotoRow> photos = await results.ReadAsync<PhotoRow>();
        return ToResponse(bus, seatClasses.ToList(), photos, _photoAddresses);
    }

    private static GetPublicBusResponse ToResponse(
        BusRow bus, List<string> seatClasses, IEnumerable<PhotoRow> photoRows, PhotoAddresses photoAddresses)
    {
        var photos = new List<PublicBusPhoto>();
        foreach (PhotoRow photo in photoRows)
        {
            photos.Add(new PublicBusPhoto(photo.PhotoId, photo.Position, photoAddresses.GetAddress(photo.FileLocation)));
        }

        return new GetPublicBusResponse
        {
            BusId = bus.BusId,
            Name = bus.Name,
            BusType = bus.BusType,
            IsAc = bus.IsAc,
            Amenities = ToAmenities(bus.Amenities),
            SeatClasses = seatClasses,
            TotalSeats = bus.TotalSeats,
            OperatorName = bus.OperatorName,
            OperatorRating = bus.OperatorRating,
            Photos = photos,
        };
    }

    // A bus stored without amenities has a null array, which the contract shows as an empty list.
    private static List<Amenity> ToAmenities(string[]? stored)
    {
        var amenities = new List<Amenity>();
        if (stored is null)
        {
            return amenities;
        }

        foreach (string amenity in stored)
        {
            amenities.Add(Enum.Parse<Amenity>(amenity));
        }

        return amenities;
    }

    private sealed class BusRow
    {
        public Guid BusId { get; init; }

        public string Name { get; init; } = string.Empty;

        public BusType BusType { get; init; }

        public bool IsAc { get; init; }

        public string[]? Amenities { get; init; }

        public string OperatorName { get; init; } = string.Empty;

        public decimal OperatorRating { get; init; }

        public int TotalSeats { get; init; }
    }

    private sealed class PhotoRow
    {
        public Guid PhotoId { get; init; }

        public int Position { get; init; }

        public string FileLocation { get; init; } = string.Empty;
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "Buses")]
[Route("public/buses")]
public sealed class GetPublicBusController : ControllerBase
{
    private readonly GetPublicBusHandler _handler;

    public GetPublicBusController(IReaderConnectionFactory readerFactory, PhotoAddresses photoAddresses)
    {
        _handler = new GetPublicBusHandler(readerFactory, photoAddresses);
    }

    /// <summary>Read a bus's public page (no sign-in).</summary>
    /// <remarks>
    /// Returns the public details of any bus, active or inactive: name, type, AC, amenities, seat
    /// classes, total seats, uploaded photos in order with CDN addresses, and the operator's company
    /// name and rating. No access token is required; one that is presented is ignored.
    /// </remarks>
    /// <param name="busId">The bus's identifier.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <response code="200">The bus's public details.</response>
    /// <response code="400">The bus identifier is not a UUID (VALIDATION_FAILED).</response>
    /// <response code="404">No bus has this identifier (NOT_FOUND).</response>
    // Anonymous on purpose: customers and visitors who have not signed in read this page from search
    // results, and it shows nothing private.
    [AllowAnonymous]
    [HttpGet("{busId}")]
    [ProducesResponseType(typeof(GetPublicBusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPublicBusAsync(Guid busId, CancellationToken cancellationToken)
    {
        GetPublicBusResponse? response = await _handler.HandleAsync(busId, cancellationToken);
        if (response is null)
        {
            return ProblemResult.NotFound(this, $"There is no bus {busId}.");
        }

        return Ok(response);
    }
}
