namespace RouteGo.Fleet.Api.Domain.OperatorProfiles;

/// <summary>
/// The rules every operator profile obeys. A profile is created once, from Identity's approval, and
/// never changed afterwards.
/// </summary>
public static class OperatorProfileRules
{
    /// <summary>The rating every new profile starts at; Identity knows nothing about ratings.</summary>
    public const decimal DefaultRating = 4.0m;

    /// <summary>The shortest company name Identity accepts.</summary>
    public const int MinCompanyNameLength = 2;

    /// <summary>The longest company name Identity accepts.</summary>
    public const int MaxCompanyNameLength = 150;

    /// <summary>A company name is 2 to 150 characters, measured as given.</summary>
    public static bool IsAcceptableCompanyName(string companyName)
    {
        return companyName.Length >= MinCompanyNameLength && companyName.Length <= MaxCompanyNameLength;
    }
}
