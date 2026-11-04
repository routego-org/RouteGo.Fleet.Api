namespace RouteGo.Fleet.Api.Infrastructure;

/// <summary>
/// The name of every environment-supplied configuration value, spelled exactly once. The names are
/// deliberately FLAT: each maps 1:1 to an environment variable of the same spelling, so no ':' or
/// '__' separator can be mistyped between the deployment template, the pipeline, and the reader
/// here. LocalEntryPoints and every consumer go through these constants, so a rename is a compile
/// error rather than a value that silently arrives null (references/configuration.md).
/// </summary>
public static class ConfigKeys
{
    /// <summary>The AWS SDK's own region variable, and the ONLY region source the app reads.
    /// Lambda sets it; LocalEntryPoints seeds it on a laptop. There is deliberately no app-owned
    /// AWS:Region key that could disagree with the runtime's own value.</summary>
    public const string AwsRegion = "AWS_REGION";

    // ══════════════════════════════════════════════════════════════════════════
    // FILLED BY the sibling slice skill and by feature work: one const per value
    // that varies by environment — a base URL, a client id, a public origin.
    //
    //   public const string BackendBaseUrl = "BackendBaseUrl";
    //
    // Adding one is four more edits, none of them a new file: a local value in
    // LocalEntryPoints, a parameter + env var in the deployment template, a
    // pipeline variable, and a RequireKeys entry if the app cannot start without
    // it. NEVER add a configuration key to an appsettings*.json.
    //
    // Otel:Exporter:* and AWS:Profile do NOT belong here — see
    // references/configuration.md for why those two keep their nested names.
    // ══════════════════════════════════════════════════════════════════════════
    /// <summary>Which Lambda execution model this process runs on — Lambda (one invocation per process,
    /// frozen between them) or ManagedInstances (many concurrent invocations in one always-on process).
    /// It picks the database pool profile (Infrastructure/Pgsql/ConnectionProfile). Set by the deployment
    /// template, never sniffed from the runtime. Required.</summary>
    public const string DbConnectionProfile = "DB_CONNECTION_PROFILE";

    /// <summary>The most database connections one process may hold: 2-4 on Lambda (a handler's opens are
    /// sequential), at least the per-environment concurrency on ManagedInstances. Set by the deployment
    /// template beside DbConnectionProfile. Required.</summary>
    public const string DbMaxPoolSize = "DB_MAX_POOL_SIZE";

    /// <summary>The issuer Identity writes into every access token; a token from anyone else is
    /// refused. Parameter Store: /routego/prod/fleet/identity-issuer. Required.</summary>
    public const string IdentityIssuer = "IdentityIssuer";

    /// <summary>The audience Identity writes into every access token. Parameter Store:
    /// /routego/prod/fleet/identity-audience. Required.</summary>
    public const string IdentityAudience = "IdentityAudience";

    /// <summary>Where Identity publishes the public keys its access tokens are signed with.
    /// Parameter Store: /routego/prod/fleet/identity-jwks-url. Required.</summary>
    public const string IdentityJwksUrl = "IdentityJwksUrl";

    /// <summary>The CDN address bus photos are served from; a photo's address is this followed by
    /// its place in the media store. Parameter Store: /routego/prod/fleet/cdn-base-url. Required.</summary>
    public const string CdnBaseUrl = "CdnBaseUrl";

    /// <summary>The private bucket bus photos are uploaded to and kept in; only the CDN reads from it.
    /// Parameter Store: /routego/prod/fleet/media-store-bucket. Required.</summary>
    public const string MediaStoreBucket = "MediaStoreBucket";

    /// <summary>An AWS-compatible endpoint to use instead of AWS for S3, SNS and SQS — set only on a
    /// developer machine, where LocalStack stands in for all three. Unset when deployed, so the SDK
    /// reaches real AWS with the execution role. Optional.</summary>
    public const string AwsServiceUrl = "AwsServiceUrl";

    /// <summary>The queue Fleet reads Identity's OperatorApproved events from (subscribed to Identity's
    /// event topic). Parameter Store: /routego/prod/fleet/fleet-queue-url. Required.</summary>
    public const string FleetQueueUrl = "FleetQueueUrl";

    /// <summary>The topic Fleet publishes its trip events to — the fleet events stream. Parameter
    /// Store: /routego/prod/fleet/fleet-events-topic-arn. Required.</summary>
    public const string FleetEventsTopicArn = "FleetEventsTopicArn";
}
