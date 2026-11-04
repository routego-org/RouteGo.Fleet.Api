using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using RouteGo.Fleet.Api.Common.Ports;
using RouteGo.Fleet.Api.Infrastructure;

namespace RouteGo.Fleet.Api.Tests.Support;

/// <summary>
/// Access tokens shaped exactly as RouteGo.Identity.Api issues them (RS256; sub, email, role and,
/// for operators, operatorId), signed with a key only the test host trusts — so a slice test
/// exercises the real bearer validation without an Identity instance to download keys from.
/// </summary>
public static class TestAccessTokens
{
    // The issuer and audience LocalEntryPoints seeds for a Development host.
    private const string Issuer = "http://localhost:5001";
    private const string Audience = "routego";

    // The host checks expiry against real time with no grace, so tokens are minted against it too.
    private static readonly IClock _clock = new SystemClock();

    private static readonly RsaSecurityKey _trustedKey = CreateKey("fleet-tests-trusted");
    private static readonly RsaSecurityKey _untrustedKey = CreateKey("fleet-tests-untrusted");

    /// <summary>Makes the host trust the test signing key instead of downloading Identity's keys.</summary>
    public static void TrustTestSigningKey(IServiceCollection services)
    {
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var configuration = new OpenIdConnectConfiguration();
            configuration.SigningKeys.Add(_trustedKey);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
        });
    }

    /// <summary>A valid token for an Admin or a Customer.</summary>
    public static string Create(string role)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Guid.NewGuid().ToString(),
            ["email"] = "person@example.com",
            ["role"] = role,
        };
        return Sign(claims, _trustedKey);
    }

    /// <summary>A valid token for an Operator, whose operatorId is also its sub.</summary>
    public static string CreateOperator(Guid operatorId)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = operatorId.ToString(),
            ["email"] = "owner@busco.in",
            ["role"] = "Operator",
            ["operatorId"] = operatorId.ToString(),
        };
        return Sign(claims, _trustedKey);
    }

    /// <summary>A well-formed token signed by a key Fleet does not trust.</summary>
    public static string CreateWithUntrustedKey(string role)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Guid.NewGuid().ToString(),
            ["email"] = "person@example.com",
            ["role"] = role,
        };
        return Sign(claims, _untrustedKey);
    }

    /// <summary>A request carrying the access token as a bearer credential.</summary>
    public static HttpRequestMessage Authorized(HttpMethod method, string route, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static string Sign(Dictionary<string, object> claims, RsaSecurityKey key)
    {
        DateTime now = _clock.UtcNow.UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(15),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static RsaSecurityKey CreateKey(string keyId)
    {
        var rsa = RSA.Create(2048);
        return new RsaSecurityKey(rsa) { KeyId = keyId };
    }
}
