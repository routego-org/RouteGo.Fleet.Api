namespace RouteGo.Fleet.Api.Infrastructure.Messaging;

/// <summary>
/// Reads an SQS redrive policy — <c>{"deadLetterTargetArn":"…","maxReceiveCount":5}</c> — whose
/// receive limit SQS writes either as a number or as a string.
/// </summary>
public static class RedrivePolicy
{
    /// <summary>The policy's maxReceiveCount, or null when there is no usable policy.</summary>
    public static int? ReadMaxReceiveCount(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy))
        {
            return null;
        }

        using var document = JsonDocument.Parse(policy);
        if (!document.RootElement.TryGetProperty("maxReceiveCount", out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt32();
        }

        bool isNumber = int.TryParse(value.GetString(), out int maxReceiveCount);
        if (!isNumber)
        {
            return null;
        }

        return maxReceiveCount;
    }
}
