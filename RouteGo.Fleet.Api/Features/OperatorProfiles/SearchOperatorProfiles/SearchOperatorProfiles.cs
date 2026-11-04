namespace RouteGo.Fleet.Api.Features.OperatorProfiles.SearchOperatorProfiles;

/// <summary>Which operator profiles to search for, and the page wanted.</summary>
public sealed class SearchOperatorProfilesRequest
{
    /// <summary>Text the company name must contain, ignoring case; every profile when absent.</summary>
    public string? CompanyName { get; init; }

    /// <summary>1-based page number; 1 when absent.</summary>
    public int? Page { get; init; }

    /// <summary>Items per page, 1–200; 20 when absent.</summary>
    public int? PageSize { get; init; }
}

/// <summary>Fleet's record of one approved bus company.</summary>
public sealed class OperatorProfile
{
    /// <summary>Identity's operator account identifier and the operatorId token claim.</summary>
    public required string OperatorId { get; init; }

    public required string CompanyName { get; init; }

    /// <summary>The operator's sign-in email.</summary>
    public required string Email { get; init; }

    /// <summary>The operator rating, 0.0 to 5.0 with one decimal place.</summary>
    public required decimal Rating { get; init; }

    /// <summary>When Identity approved the operator.</summary>
    public required DateTimeOffset ApprovedAt { get; init; }
}

/// <summary>One page of matching profiles, sorted by company name, then operator identifier.</summary>
public sealed class SearchOperatorProfilesResponse
{
    public required IReadOnlyList<OperatorProfile> Items { get; init; }

    /// <summary>How many profiles match across all pages.</summary>
    public required int TotalCount { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }
}

public sealed class SearchOperatorProfilesValidator : AbstractValidator<SearchOperatorProfilesRequest>
{
    public const int MaxCompanyNameLength = 150;

    public SearchOperatorProfilesValidator()
    {
        RuleFor(x => x.CompanyName).Length(1, MaxCompanyNameLength).When(x => x.CompanyName != null)
            .WithMessage("The company name must be 1 to 150 characters.");
        RuleFor(x => x.Page).Must(Paging.IsAcceptablePage).WithMessage(Paging.PageMessage);
        RuleFor(x => x.PageSize).Must(Paging.IsAcceptablePageSize).WithMessage(Paging.PageSizeMessage);
    }
}

public sealed class SearchOperatorProfilesHandler
{
    // strpos rather than LIKE, so a % or _ in the filter is matched as written. The operator id
    // breaks ties between equal names, so paging never repeats or skips a profile.
    private const string SelectPageSql = """
        SELECT count(*)::int
        FROM operator_profiles.operator_profiles
        WHERE CAST(@CompanyName AS text) IS NULL OR strpos(lower(company_name), lower(@CompanyName)) > 0;

        SELECT operator_id, company_name, email, rating, approved_at
        FROM operator_profiles.operator_profiles
        WHERE CAST(@CompanyName AS text) IS NULL OR strpos(lower(company_name), lower(@CompanyName)) > 0
        ORDER BY lower(company_name), operator_id
        OFFSET @Offset LIMIT @Limit;
        """;

    private readonly IReaderConnectionFactory _readerFactory;

    public SearchOperatorProfilesHandler(IReaderConnectionFactory readerFactory)
    {
        _readerFactory = readerFactory;
    }

    /// <summary>Lists one page of the operator profiles whose company name contains the filter.</summary>
    public async Task<SearchOperatorProfilesResponse> HandleAsync(SearchOperatorProfilesRequest request, CancellationToken cancellationToken)
    {
        PageRequest page = Paging.Normalize(request.Page, request.PageSize);
        return await _readerFactory.ExecuteReadAsync(
            connection => ReadAsync(connection, request.CompanyName, page, cancellationToken), cancellationToken);
    }

    private static async Task<SearchOperatorProfilesResponse> ReadAsync(
        DbConnection connection, string? companyName, PageRequest page, CancellationToken cancellationToken)
    {
        var parameters = new { CompanyName = companyName, page.Offset, Limit = page.PageSize };
        await using SqlMapper.GridReader results = await connection.QueryMultipleAsync(new CommandDefinition(
            SelectPageSql, parameters, cancellationToken: cancellationToken));
        int totalCount = await results.ReadSingleAsync<int>();
        IEnumerable<ProfileRow> rows = await results.ReadAsync<ProfileRow>();
        var items = rows.Select(ToProfile).ToList();
        return new SearchOperatorProfilesResponse { Items = items, TotalCount = totalCount, Page = page.Page, PageSize = page.PageSize };
    }

    private static OperatorProfile ToProfile(ProfileRow row)
    {
        var approvedAt = DateTime.SpecifyKind(row.ApprovedAt, DateTimeKind.Utc);
        return new OperatorProfile
        {
            OperatorId = row.OperatorId,
            CompanyName = row.CompanyName,
            Email = row.Email,
            Rating = row.Rating,
            ApprovedAt = new DateTimeOffset(approvedAt),
        };
    }

    private sealed class ProfileRow
    {
        public string OperatorId { get; init; } = string.Empty;

        public string CompanyName { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public decimal Rating { get; init; }

        public DateTime ApprovedAt { get; init; }
    }
}

[ApiController]
[ApiExplorerSettings(GroupName = "OperatorProfiles")]
[Route("admin")]
public sealed class SearchOperatorProfilesController : ControllerBase
{
    private readonly SearchOperatorProfilesHandler _handler;
    private readonly SearchOperatorProfilesValidator _validator = new SearchOperatorProfilesValidator();

    public SearchOperatorProfilesController(IReaderConnectionFactory readerFactory)
    {
        _handler = new SearchOperatorProfilesHandler(readerFactory);
    }

    /// <summary>Search operator profiles (Admin only).</summary>
    /// <remarks>
    /// Lists the operator profiles Fleet holds whose company name contains the given text (ignoring
    /// case), or every profile when none is given, sorted by company name (ignoring case), then
    /// operator identifier. Read only. Requires role Admin. No match is an empty page, not an error.
    /// </remarks>
    /// <param name="request">The company-name filter and the page wanted.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <response code="200">The requested page.</response>
    /// <response code="400">The company name, the page or the page size is invalid (VALIDATION_FAILED).</response>
    /// <response code="401">No valid access token was presented (UNAUTHORIZED).</response>
    /// <response code="403">The caller is not an admin (FORBIDDEN).</response>
    [Authorize(Policy = Policies.Admin)]
    [HttpGet("operatorProfiles")]
    [ProducesResponseType(typeof(SearchOperatorProfilesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SearchOperatorProfilesAsync(
        [FromQuery] SearchOperatorProfilesRequest request, CancellationToken cancellationToken)
    {
        ValidationResult validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return ValidationProblem(validation.ToModelState());
        }

        SearchOperatorProfilesResponse response = await _handler.HandleAsync(request, cancellationToken);
        return Ok(response);
    }
}
