namespace RouteGo.Fleet.Api.Common;

/// <summary>
/// The page and page-size limits every list operation shares, written once. A value outside the
/// limits is refused by the slice's validator rather than quietly adjusted, because the published
/// contracts list a validation failure for it; <see cref="Normalize"/> only fills in the defaults.
/// </summary>
public static class Paging
{
    public const int FirstPage = 1;

    public const int MinPageSize = 1;

    public const int MaxPageSize = 200;

    public const int DefaultPageSize = 20;

    public const string PageMessage = "The page must be 1 or more.";

    public const string PageSizeMessage = "The page size must be from 1 to 200.";

    /// <summary>True when the page number is absent or 1-based.</summary>
    public static bool IsAcceptablePage(int? page)
    {
        return page is null || page.Value >= FirstPage;
    }

    /// <summary>True when the page size is absent or within the shared limits.</summary>
    public static bool IsAcceptablePageSize(int? pageSize)
    {
        return pageSize is null || (pageSize.Value >= MinPageSize && pageSize.Value <= MaxPageSize);
    }

    /// <summary>The page wanted, with the first page and the default size standing in for absent values.</summary>
    public static PageRequest Normalize(int? page, int? pageSize)
    {
        int number = FirstPage;
        if (page != null)
        {
            number = page.Value;
        }

        int size = DefaultPageSize;
        if (pageSize != null)
        {
            size = pageSize.Value;
        }

        return new PageRequest(number, size);
    }
}
