namespace RouteGo.Fleet.Api.Infrastructure;

/// <summary>
/// Loads this service's non-secret settings (media bucket, CDN base URL, trip-generation window,
/// photo limits) from SSM Parameter Store when the app runs anywhere but Development, so an operator
/// can change them without a rebuild. Credentials come from the default AWS chain — the EC2 instance
/// role when deployed.
/// </summary>
public static class ParameterStoreConfiguration
{
    // One environment ("prod") exists, so the path is a constant rather than a setting that could
    // point a deployed service at another service's parameters.
    private const string ParameterPath = "/routego/prod/fleet/";

    /// <summary>
    /// Adds the Parameter Store source outside Development. The source is required: a service that
    /// cannot read its parameters must fail at startup, not run on missing values.
    /// </summary>
    /// <param name="configuration">The application's configuration builder.</param>
    /// <param name="environment">The hosting environment; Development never calls AWS.</param>
    public static void AddFleetParameters(
        this IConfigurationBuilder configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        configuration.AddSystemsManager(source =>
        {
            source.Path = ParameterPath;
            source.Optional = false;
            source.ParameterProcessor = new FlatKeyParameterProcessor();
        });
    }
}
