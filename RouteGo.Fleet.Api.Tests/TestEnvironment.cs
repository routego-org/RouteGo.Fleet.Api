using System.Runtime.CompilerServices;

namespace RouteGo.Fleet.Api.Tests;

/// <summary>
/// Marks the whole test process as Development before any host is built. ApiFactory pins the
/// host's environment through the builder, but LocalEntryPoints runs before the builder exists and
/// reads the raw variable — without this it never seeds the local values the host requires.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void MarkDevelopment()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
    }
}
