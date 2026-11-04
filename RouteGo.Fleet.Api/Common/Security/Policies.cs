namespace RouteGo.Fleet.Api.Common.Security;

/// <summary>The authorization policies endpoints name.</summary>
public static class Policies
{
    /// <summary>The access token's role claim is Operator.</summary>
    public const string Operator = "Operator";

    /// <summary>The access token's role claim is Admin.</summary>
    public const string Admin = "Admin";
}
