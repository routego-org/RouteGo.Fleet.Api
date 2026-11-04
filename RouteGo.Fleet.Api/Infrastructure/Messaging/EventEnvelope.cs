using System.Globalization;

namespace RouteGo.Fleet.Api.Infrastructure.Messaging;

/// <summary>
/// The RouteGo event envelope read from a raw queue message body:
/// <c>{ eventId, eventType, version, occurredAt, correlationId, producer, data }</c>.
/// <see cref="Data"/> is passed on as sent; whether it is usable is for the event's handler to say.
/// </summary>
public sealed record EventEnvelope
{
    /// <summary>The event's identity.</summary>
    public required string EventId { get; init; }

    /// <summary>What happened, for example OperatorApproved.</summary>
    public required string EventType { get; init; }

    /// <summary>When it happened, in UTC.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The correlation ID of the request that caused it.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>The event-specific data, or an undefined element when the envelope carries none.</summary>
    public required JsonElement Data { get; init; }

    /// <summary>
    /// Reads <paramref name="body"/>, or returns null when it is not an envelope with an event ID,
    /// event type, time it occurred and correlation ID.
    /// </summary>
    public static EventEnvelope? Read(string body)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadFields(root);
    }

    private static EventEnvelope? ReadFields(JsonElement root)
    {
        string? eventId = ReadText(root, "eventId");
        string? eventType = ReadText(root, "eventType");
        string? correlationId = ReadText(root, "correlationId");
        DateTimeOffset? occurredAt = ReadTime(root, "occurredAt");
        if (eventId is null || eventType is null || correlationId is null || occurredAt is null)
        {
            return null;
        }

        root.TryGetProperty("data", out JsonElement data);
        return new EventEnvelope
        {
            EventId = eventId,
            EventType = eventType,
            OccurredAt = occurredAt.Value,
            CorrelationId = correlationId,
            Data = data,
        };
    }

    private static string? ReadText(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text;
    }

    private static DateTimeOffset? ReadTime(JsonElement root, string propertyName)
    {
        string? text = ReadText(root, propertyName);
        if (text is null)
        {
            return null;
        }

        bool isTime = DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset time);
        if (!isTime)
        {
            return null;
        }

        return time.ToUniversalTime();
    }
}
