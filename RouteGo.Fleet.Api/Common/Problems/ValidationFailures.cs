using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace RouteGo.Fleet.Api.Common.Problems;

/// <summary>
/// Hands a slice validator's failures to <c>ValidationProblem(ModelStateDictionary)</c>. That
/// overload builds the 400 through the host's problem-details factory, where
/// <see cref="ProblemDetailsCustomization"/> stamps the cataloged type and code — the same path
/// the framework's own model-binding failures take, so every 400 has one shape.
/// </summary>
public static class ValidationFailures
{
    /// <summary>The failures keyed by field path, as the request schema names the fields.</summary>
    public static ModelStateDictionary ToModelState(this ValidationResult validation)
    {
        var modelState = new ModelStateDictionary();
        foreach (ValidationFailure failure in validation.Errors)
        {
            modelState.AddModelError(failure.PropertyName, failure.ErrorMessage);
        }

        return modelState;
    }
}
