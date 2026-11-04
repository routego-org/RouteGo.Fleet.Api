namespace RouteGo.Fleet.Api.Common.Behaviors;

/// <summary>
/// Accepts the caller's X-Correlation-Id or generates one, returns it on the response, opens a
/// logging scope carrying it, and makes it available to controllers through
/// <see cref="GetCorrelationId"/> so every event a request records carries the same ID.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    /// <summary>The header a caller sends, and the response carries back.</summary>
    public const string HeaderName = "X-Correlation-Id";

    // Long enough for a UUID or a caller's trace ID, short enough that a hostile header cannot
    // bloat every log line and event that copies it.
    private const int MaxLength = 64;

    private static readonly object _itemKey = new object();

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>The correlation ID of the current request.</summary>
    /// <param name="context">The current request.</param>
    /// <returns>The accepted or generated ID; never empty once the middleware has run.</returns>
    public static string GetCorrelationId(HttpContext context)
    {
        if (context.Items.TryGetValue(_itemKey, out object? value) && value is string correlationId)
        {
            return correlationId;
        }

        throw new InvalidOperationException("CorrelationIdMiddleware has not run for this request.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId = ChooseCorrelationId(context.Request.Headers[HeaderName].ToString());
        context.Items[_itemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        var scope = new Dictionary<string, object> { ["CorrelationId"] = correlationId };
        using (_logger.BeginScope(scope))
        {
            await _next(context);
        }
    }

    private static string ChooseCorrelationId(string supplied)
    {
        if (IsAcceptable(supplied))
        {
            return supplied;
        }

        return Guid.NewGuid().ToString();
    }

    // Only characters that are safe to copy verbatim into logs, headers and event envelopes.
    private static bool IsAcceptable(string supplied)
    {
        if (supplied.Length == 0 || supplied.Length > MaxLength)
        {
            return false;
        }

        foreach (char character in supplied)
        {
            bool isSafe = char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_' || character == '.';
            if (!isSafe)
            {
                return false;
            }
        }

        return true;
    }
}
