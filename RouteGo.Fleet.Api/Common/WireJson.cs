using System.Text.Json.Serialization;

namespace RouteGo.Fleet.Api.Common;

/// <summary>
/// The ONE wire-format contract for JSON this app reads or writes outside ASP.NET Core's own
/// response pipeline: HttpClient calls (System.Net.Http.Json overloads take it), event-payload
/// deserialization in a Lambda worker, any hand-rolled JsonSerializer use. camelCase property
/// names, camelCase string enums, case-insensitive reads — exactly the names the authored
/// API/event contracts declare. ASP.NET Core's own response serialization gets the same enum
/// policy via AddJsonOptions in Program.cs, so authored contract, responses, and reads all
/// agree by construction.
/// Never build per-call options (a new JsonSerializerOptions per call discards the serializer's
/// internal cache) and never use [JsonPropertyName]/[JsonStringEnumMemberName] overrides —
/// contract names map 1:1 by policy, not by attribute.
/// </summary>
public static class WireJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>
    /// Reads an enum from its wire spelling — the camelCase name the contracts declare — for values
    /// that arrive outside a JSON body, such as a query parameter. The framework's own binding is
    /// not used for these because it also accepts PascalCase names and bare numbers.
    /// </summary>
    /// <param name="wireValue">The value as sent, for example "active".</param>
    /// <param name="value">The matching enum member when the value is valid.</param>
    /// <returns>True when the value is one of the enum's contract spellings.</returns>
    public static bool TryReadEnum<TEnum>(string? wireValue, out TEnum value)
        where TEnum : struct, Enum
    {
        foreach (TEnum candidate in Enum.GetValues<TEnum>())
        {
            string wireName = JsonNamingPolicy.CamelCase.ConvertName(candidate.ToString());
            if (wireName == wireValue)
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
