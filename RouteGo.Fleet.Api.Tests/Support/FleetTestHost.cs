using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RouteGo.Fleet.Api.Features.Trips.PublishFleetEvents;
using RouteGo.Fleet.Api.Infrastructure.Messaging;

namespace RouteGo.Fleet.Api.Tests.Support;

/// <summary>
/// The real host over the test database, trusting the test signing key so slice tests can call
/// it with tokens from <see cref="TestAccessTokens"/>. The background queue listener and event
/// publisher are left out: an HTTP test asserts on the events a request recorded, and a publisher
/// running underneath it would race the assertion. Their own tests drive them directly.
/// </summary>
internal static class FleetTestHost
{
    public static ApiFactory For(DatabaseFixture database, params Action<IServiceCollection>[] overrides)
    {
        var all = new List<Action<IServiceCollection>> { TestAccessTokens.TrustTestSigningKey, RemoveBackgroundWork };
        all.AddRange(overrides);
        return TestHost.For(database, all.ToArray());
    }

    private static void RemoveBackgroundWork(IServiceCollection services)
    {
        var backgroundWork = services.Where(IsBackgroundWork).ToList();
        foreach (ServiceDescriptor descriptor in backgroundWork)
        {
            services.Remove(descriptor);
        }
    }

    private static bool IsBackgroundWork(ServiceDescriptor descriptor)
    {
        if (descriptor.ServiceType != typeof(IHostedService))
        {
            return false;
        }

        // Program.cs registers exactly its two background workers through factories (each is handed
        // the system actor's writer), so they are the hosted services with a factory; the framework's
        // own hosted services are registered by type and stay.
        if (descriptor.ImplementationFactory != null)
        {
            return true;
        }

        return descriptor.ImplementationType == typeof(FleetQueueListener)
            || descriptor.ImplementationType == typeof(PublishFleetEventsService);
    }
}
