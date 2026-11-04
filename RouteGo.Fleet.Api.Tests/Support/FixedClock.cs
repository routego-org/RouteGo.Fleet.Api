using Microsoft.Extensions.DependencyInjection;
using RouteGo.Fleet.Api.Common.Ports;

namespace RouteGo.Fleet.Api.Tests.Support;

/// <summary>
/// A clock stopped at one instant, for tests that assert a time the app stamps. Register it over
/// the host's clock with <see cref="Override"/>.
/// </summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset utcNow)
    {
        UtcNow = utcNow;
    }

    public DateTimeOffset UtcNow { get; }

    /// <summary>A host override that replaces the system clock with one stopped at <paramref name="utcNow"/>.</summary>
    public static Action<IServiceCollection> Override(DateTimeOffset utcNow)
    {
        var clock = new FixedClock(utcNow);
        return services => services.AddSingleton<IClock>(clock);
    }
}
