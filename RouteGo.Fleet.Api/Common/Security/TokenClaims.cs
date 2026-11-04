namespace RouteGo.Fleet.Api.Common.Security;

/// <summary>The claim names RouteGo.Identity.Api writes into every access token, kept as issued.</summary>
public static class TokenClaims
{
    /// <summary>The signed-in account's email.</summary>
    public const string Email = "email";

    /// <summary>Operator, Admin or Customer.</summary>
    public const string Role = "role";

    /// <summary>The operator's identifier; present only on Operator tokens. Owns every bus, route and schedule.</summary>
    public const string OperatorId = "operatorId";
}
