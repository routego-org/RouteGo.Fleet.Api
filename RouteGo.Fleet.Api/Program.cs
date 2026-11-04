using System.Text.Json.Serialization;
using Amazon.S3;
using Amazon.SQS;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
// FILLED BY the sibling slice skill — the usings for what it wires below (e.g. using RouteGo.Fleet.Api.Infrastructure.Pgsql;
// when the first data slice lands). Non-System usings only: System.* sort first (IDE0055).
using RouteGo.Fleet.Api.Features.Trips.PublishFleetEvents;
using RouteGo.Fleet.Api.Infrastructure.Messaging;
using RouteGo.Fleet.Api.Infrastructure.Pgsql;
using RouteGo.Fleet.Api.Infrastructure;
using Scalar.AspNetCore;

// Configuration reaches this app through ONE channel in every environment: environment variables,
// flat-named so there is no ':' / '__' separator to mistype (Infrastructure/ConfigKeys.cs). The
// deployed values come from the deployment template; the local ones from LocalEntryPoints, which
// MUST run before the builder is created — its environment-variable provider snapshots the process
// block once, so anything set after that line never reaches IConfiguration
// (references/configuration.md).
LocalEntryPoints.Apply();
// FILLED BY the sibling slice skill when the first data slice lands — BEFORE the builder, so nothing
// can query first: Dapper bakes its name mapping per (query, type) at first execution, so a late call
// is a silent no-op (skills-db-postgres #11/#25; its check_dapper_mapping.py D3 anchors on this line).
// The timeout is DERIVED from this API's caller deadline, with the derivation in a why-comment here:
//   DapperConfiguration.Apply(commandTimeout: TimeSpan.FromSeconds(...));
// 10 s: inside the caller's own deadline, so a query that runs past it has already failed upstream.
DapperConfiguration.Apply(commandTimeout: TimeSpan.FromSeconds(10));

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Deployed, the non-secret settings live in SSM Parameter Store so they change without a rebuild;
// loaded after the environment variables so the parameter store is the source of truth there.
// Development never calls AWS — LocalEntryPoints supplies the same keys.
builder.Configuration.AddFleetParameters(builder.Environment);

// DI scope validation, on in EVERY environment (skills-db-postgres #26). CreateBuilder turns
// these on in Development only, so a captive dependency throws on a laptop and goes unnoticed in
// production: a singleton that takes a scoped IWriterConnectionFactory or ICurrentActor resolves
// it from the ROOT scope and freezes the first request's actor for the life of the process, and
// every later user's writes are stamped with that identity. ValidateOnBuild catches the capture
// at startup; ValidateScopes catches a scoped service pulled from app.Services at runtime.
// A startup failure here is the finding, not a regression - read the exception, fix the capture.
builder.Host.UseDefaultServiceProvider((_, options) =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

// Fail at startup naming every missing key at once, rather than on whichever request first needs
// one. List only what the app cannot start without; a legitimately optional key is left out.
// FILLED BY the sibling slice skill as it introduces keys:
//   builder.Configuration.RequireKeys(ConfigKeys.BackendBaseUrl, ConfigKeys.ClientId);
builder.Configuration.RequireKeys(ConfigKeys.DbConnectionProfile, ConfigKeys.DbMaxPoolSize);
builder.Configuration.RequireKeys(ConfigKeys.IdentityIssuer, ConfigKeys.IdentityAudience, ConfigKeys.IdentityJwksUrl);
builder.Configuration.RequireKeys(ConfigKeys.CdnBaseUrl, ConfigKeys.MediaStoreBucket);
builder.Configuration.RequireKeys(ConfigKeys.FleetQueueUrl, ConfigKeys.FleetEventsTopicArn);

// Log levels, in code for the same reason every other setting is: one place, and no appsettings
// file to disagree with it. Everything defaults to Information; the framework's own request/routing
// chatter stays at Warning so the log group is not flooded, except in Development where it
// is the most useful thing in the console. A deployed run can still be turned up without a code
// change: Logging__LogLevel__Default is an ordinary environment variable on the container.
LogLevel frameworkLogLevel = LogLevel.Warning;
if (builder.Environment.IsDevelopment())
{
    frameworkLogLevel = LogLevel.Information;
}

builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", frameworkLogLevel);

// Secrets: standard AWS Secrets Manager access (references/secrets.md). ONE fixed name per secret;
// the SOURCE is the environment — Development reads configuration, everything else Secrets Manager.
builder.Services.AddSingleton<SecretResolver>();

// Wire naming contract (references/host-components.md): camelCase properties are the web
// default; enums serialize as camelCase STRINGS (without this converter they serialize as
// numbers and every enum in every response breaks the authored contract). Same policy as
// Common/WireJson.cs, which covers HttpClient reads and hand-rolled serialization.
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// Errors: RFC 7807 ProblemDetails + unhandled-exception handler (references/host-components.md).
// Every 400 — a slice validator's or the framework's model binding's — gets the cataloged
// VALIDATION_FAILED type and code here (Common/Problems/ProblemDetailsCustomization.cs).
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ProblemDetailsCustomization.Apply);
builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();

// Infrastructure ports — registered once. No per-slice registrations.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton(new PhotoAddresses(builder.Configuration.RequireBaseAddress(ConfigKeys.CdnBaseUrl)));
builder.Services.AddSingleton(AwsClients.CreateS3(builder.Configuration));
builder.Services.AddSingleton<IMediaStore>(provider => new S3MediaStore(
    provider.GetRequiredService<IAmazonS3>(), builder.Configuration[ConfigKeys.MediaStoreBucket]!));
// FILLED BY skills-aspnetcore-api-vertical-slice when the first data slice lands:
//   the reader/writer connection-port registrations (files copied from skills-db-postgres —
//   reader singleton; writer scoped, it carries the per-request actor) (+ any other ports).
// Database (skills-db-postgres, Setting up a project, step 2). The secret's NAME is a fixed platform
// constant; the SOURCE is the environment — Development reads user-secrets, anything else Secrets
// Manager through the execution role — and the one shared mapper turns its JSON into a connection string.
string? databaseSecretJson = await new SecretResolver(builder.Configuration, builder.Environment)
    .ResolveAsync(DbSecret.SecretId);
if (string.IsNullOrEmpty(databaseSecretJson))
{
    string databaseSecretHelp = $"grant the execution role secretsmanager:GetSecretValue on '{DbSecret.SecretId}'";
    if (builder.Environment.IsDevelopment())
    {
        databaseSecretHelp = $"set it once: dotnet user-secrets set \"{DbSecret.SecretId}\" '<the platform JSON>' --id routego";
    }
    throw new InvalidOperationException($"No database secret — {databaseSecretHelp}.");
}
// The pool profile rides on the same seam: the deployment template names the Lambda execution model
// (DB_CONNECTION_PROFILE, DB_MAX_POOL_SIZE) and the profile turns that into pool settings plus whether a
// dropped connection also clears the idle pool (Infrastructure/Pgsql/ConnectionProfile.cs).
var databaseProfile = ConnectionProfile.FromConfiguration(builder.Configuration);
string databaseConnectionString = databaseProfile.Apply(DbSecretValue.ToConnectionString(databaseSecretJson));
// ActorClaimOptions defaults match the RouteGo token; builder.Services.Configure<ActorClaimOptions>(...) overrides them.
// RouteGo access tokens carry no name claim (sub, email, role, operatorId only), so the audit
// records the signed-in account's email as its name.
builder.Services.Configure<ActorClaimOptions>(options => options.NameClaim = TokenClaims.Email);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentActor, HttpContextActor>();
builder.Services.AddSingleton<IReaderConnectionFactory>(new ReaderConnectionFactory(databaseConnectionString));
builder.Services.AddScoped<IWriterConnectionFactory>(provider => new WriterConnectionFactory(
    databaseConnectionString, provider.GetRequiredService<ICurrentActor>(), databaseProfile.Retry));
// Background work has no request and so no caller: its writes are stamped with the reserved system
// actor. Built once here and handed to each worker below rather than registered, so no request can
// resolve it and no worker can resolve the request-scoped writer by mistake.
var backgroundWriterFactory = new WriterConnectionFactory(databaseConnectionString, SystemActor.Default, databaseProfile.Retry);

// Events in and out (LocalStack in Development). Identity's OperatorApproved events wait on the fleet
// queue until the listener turns them into operator profiles; recorded trip events leave through SNS
// on a five-second clock. Both run in-process, and operator requests never wait for either.
builder.Services.AddSingleton(AwsClients.CreateSqs(builder.Configuration));
builder.Services.AddSingleton(new SnsFleetEventPublisher(
    AwsClients.CreateSns(builder.Configuration), builder.Configuration[ConfigKeys.FleetEventsTopicArn]!));
// Constructed here because each needs the system actor's writer, which the container does not hold.
// (A keyed singleton beside the scoped writer was tried: the container returned the scoped writer for
// the keyed lookup, and start-up validation refused to run.)
builder.Services.AddHostedService(provider => new FleetQueueListener(
    provider.GetRequiredService<IAmazonSQS>(),
    provider.GetRequiredService<IConfiguration>(),
    backgroundWriterFactory,
    provider.GetRequiredService<ILoggerFactory>()));
builder.Services.AddHostedService(provider => new PublishFleetEventsService(
    backgroundWriterFactory,
    provider.GetRequiredService<SnsFleetEventPublisher>(),
    provider.GetRequiredService<IClock>(),
    provider.GetRequiredService<ILoggerFactory>()));

// Readiness checks, tagged so liveness (/health) never depends on them.
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: new[] { ReadinessTag })
    .AddCheck<FleetQueueHealthCheck>("fleet-queue", tags: new[] { ReadinessTag });

// Access tokens are issued by RouteGo.Identity.Api and checked here exactly as Identity checks its
// own: RS256 signature against Identity's published keys, issuer, audience, and exact expiry with no
// extra minutes of grace. Claim names stay as issued (sub, email, role, operatorId). Identity is
// reached over RouteGo's private network on plain HTTP (the contracts' server addresses), so the
// key download cannot insist on HTTPS.
var signingKeys = new ConfigurationManager<OpenIdConnectConfiguration>(
    builder.Configuration[ConfigKeys.IdentityJwksUrl]!,
    new JwksConfigurationRetriever(),
    new HttpDocumentRetriever { RequireHttps = false });
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.ConfigurationManager = signingKeys;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = builder.Configuration[ConfigKeys.IdentityIssuer],
        ValidAudience = builder.Configuration[ConfigKeys.IdentityAudience],
        ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 },
        NameClaimType = TokenClaims.Email,
        RoleClaimType = TokenClaims.Role,
        ClockSkew = TimeSpan.Zero,
    };
    options.Events = new JwtBearerEvents
    {
        OnChallenge = AuthenticationProblems.WriteUnauthorizedAsync,
        OnForbidden = AuthenticationProblems.WriteForbiddenAsync,
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.Operator, policy => policy.RequireRole(Policies.Operator));
    options.AddPolicy(Policies.Admin, policy => policy.RequireRole(Policies.Admin));
});

// Validation errors name fields as the request schema does (camelCase), so a client can highlight them.
ValidatorOptions.Global.PropertyNameResolver = (_, member, _) => JsonNamingPolicy.CamelCase.ConvertName(member!.Name);

// OpenAPI: one document per Feature Area, from the explicit list (references/host-components.md).
// new_slice.py appends an area string here when a NEW area's first slice lands.
string[] areas = new[] { "Routes", "Buses", "Trips", "OperatorProfiles" };
foreach (string area in areas)
{
    builder.Services.AddOpenApi(area, o =>
    {
        string areaName = area;
        o.ShouldInclude = api =>
            api.GroupName is null || string.Equals(api.GroupName, areaName, StringComparison.OrdinalIgnoreCase);
        o.AddOperationTransformer<ResponseHeadersTransformer>();
    });
}

WebApplication app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();    // OUTERMOST — the ID is on every log line, error and event
app.UseExceptionHandler();                       // unhandled -> RFC 7807 problem+json
app.UseMiddleware<ResponseMetadataMiddleware>(); // X-Trace-Id, Server-Timing, ... on every response
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapOpenApi();                                // /openapi/{area}.json per document
if (app.Environment.IsDevelopment())             // dev-only — the doc is not the published contract
{
    app.MapScalarApiReference(o =>
    {
        o.WithTitle("API Reference");
        for (int i = 0; i < areas.Length; i++)
        {
            o.AddDocument(areas[i], areas[i], $"/openapi/{areas[i]}.json", isDefault: i == 0);
        }
    });
    app.MapGet("/", () => Results.Redirect("/scalar")); // one URL to remember
}
app.MapGet("/health", () => Results.Ok("healthy")); // liveness: answers whenever the process runs
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(ReadinessTag),
});

app.Run();

public partial class Program // exposes Program to WebApplicationFactory<Program> in tests
{
    private const string ReadinessTag = "ready";
}
