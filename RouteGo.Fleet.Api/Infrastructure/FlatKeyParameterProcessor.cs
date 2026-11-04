using System.Text;
using Amazon.Extensions.Configuration.SystemsManager;
using Amazon.SimpleSystemsManagement.Model;

namespace RouteGo.Fleet.Api.Infrastructure;

/// <summary>
/// Turns a kebab-case parameter name (<c>trip-generation-days</c>) into the flat PascalCase key the
/// rest of the app reads through <see cref="ConfigKeys"/> (<c>TripGenerationDays</c>), so a value
/// arrives under the same name whether it came from Parameter Store or a local environment variable.
/// </summary>
public sealed class FlatKeyParameterProcessor : DefaultParameterProcessor
{
    /// <summary>Returns the flat PascalCase configuration key for a parameter under the path.</summary>
    /// <param name="parameter">The parameter read from Parameter Store.</param>
    /// <param name="path">The path prefix the parameter was loaded from.</param>
    /// <returns>The configuration key, for example <c>TripGenerationDays</c>.</returns>
    public override string GetKey(Parameter parameter, string path)
    {
        string kebabKey = base.GetKey(parameter, path);
        string[] words = kebabKey.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var flatKey = new StringBuilder();
        foreach (string word in words)
        {
            flatKey.Append(char.ToUpperInvariant(word[0]));
            flatKey.Append(word, 1, word.Length - 1);
        }

        return flatKey.ToString();
    }
}
