using System.Diagnostics;

namespace RouteGo.Fleet.Api.Common.Problems;

/// <summary>
/// Applied to every problem response the host's problem-details factory builds: adds the trace id,
/// and gives every 400 validation problem the cataloged VALIDATION_FAILED type, title and code the
/// contract promises — whether a slice validator or the framework's model binding refused the request.
/// </summary>
public static class ProblemDetailsCustomization
{
    private const string ValidationFailedTitle = "One or more fields are invalid.";

    /// <summary>Customizes one problem response before it is written.</summary>
    public static void Apply(ProblemDetailsContext context)
    {
        ProblemDetails problem = context.ProblemDetails;
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString();
        if (problem is not ValidationProblemDetails)
        {
            return;
        }

        problem.Type = ProblemTypes.ValidationFailed.Type;
        problem.Title = ValidationFailedTitle;
        problem.Extensions["code"] = ProblemTypes.ValidationFailed.Code;
    }
}
