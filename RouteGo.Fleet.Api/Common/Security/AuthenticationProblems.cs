using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace RouteGo.Fleet.Api.Common.Security;

/// <summary>
/// Turns the bearer handler's bare 401 and 403 into the RFC 7807 bodies the contract promises,
/// with the cataloged UNAUTHORIZED and FORBIDDEN codes — the authentication middleware answers
/// before any controller runs, so the controllers' ProblemResult factories never see these.
/// </summary>
public static class AuthenticationProblems
{
    /// <summary>Answers a missing, invalid or expired access token.</summary>
    public static async Task WriteUnauthorizedAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        await WriteAsync(context.HttpContext, ProblemTypes.Unauthorized, "Sign-in required.", StatusCodes.Status401Unauthorized);
    }

    /// <summary>Answers a valid access token whose role may not perform the operation.</summary>
    public static async Task WriteForbiddenAsync(ForbiddenContext context)
    {
        await WriteAsync(context.HttpContext, ProblemTypes.Forbidden, "You may not perform this action.", StatusCodes.Status403Forbidden);
    }

    private static async Task WriteAsync(HttpContext httpContext, ProblemType problemType, string title, int status)
    {
        httpContext.Response.StatusCode = status;
        IProblemDetailsService problems = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        var problem = new ProblemDetails { Type = problemType.Type, Title = title, Status = status };
        problem.Extensions["code"] = problemType.Code;
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem });
    }
}
