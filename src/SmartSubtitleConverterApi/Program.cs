using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Scalar.AspNetCore;
using SmartSubtitleConverterApi.Binary;
using SmartSubtitleConverterApi.Conversion;
using SmartSubtitleConverterApi.Endpoints;
using SmartSubtitleConverterApi.Formats;
using SmartSubtitleConverterApi.Infrastructure;
using SmartSubtitleConverterApi.Profiles;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
var opts = ApiOptions.FromConfiguration(builder.Configuration);

// LOG_FORMAT: text (default, one plain line per entry) or json (the JSON console formatter). Invalid -> text + warning.
var logFormatRaw = builder.Configuration["LOG_FORMAT"];
var logFormat = string.IsNullOrWhiteSpace(logFormatRaw) ? "text" : logFormatRaw.Trim().Trim('"', '\'').ToLowerInvariant();
var logFormatInvalid = logFormat is not ("text" or "json");
builder.Logging.ClearProviders();
if (logFormat == "json")
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    });
else
    builder.Logging.AddConsole(o => o.FormatterName = LineConsoleFormatter.Name)
        .AddConsoleFormatter<LineConsoleFormatter, Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions>();

builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = Math.Max(opts.MaxJsonBodyBytes, opts.MaxMultipartBodyBytes); // narrowed per request below
    k.AddServerHeader = false;
});
builder.Services.Configure<FormOptions>(f =>
{
    f.MultipartBodyLengthLimit = opts.MaxMultipartBodyBytes;
    f.ValueLengthLimit = opts.ProfileMaxBytes;
    f.ValueCountLimit = 16;
});
// Binding failures (bad JSON, wrong types, bad query values) throw, so ErrorMiddleware renders problem+json (fix A5).
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(j =>
{
    j.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    j.SerializerOptions.DefaultIgnoreCondition = Json.Options.DefaultIgnoreCondition;
});

builder.Services.AddSingleton(opts);
builder.Services.AddSingleton<SmartSubtitleConverterApi.Storage.FileStorage>();
builder.Services.AddSingleton<SmartSubtitleConverterApi.Storage.S3Storage>();
builder.Services.AddSingleton(sp => new SmartSubtitleConverterApi.Storage.HttpStorage(opts));
builder.Services.AddSingleton<SmartSubtitleConverterApi.Storage.StorageRouter>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SmartSubtitleConverterApi.Jobs.JobStore>();
builder.Services.AddSingleton<FormatRegistry>();
builder.Services.AddSingleton<PresetStore>();
builder.Services.AddSingleton<ProfileResolver>();
builder.Services.AddSingleton<ConversionEngine>();
builder.Services.AddSingleton<ConversionQueue>();
builder.Services.AddSingleton<ApiKeyStore>();
builder.Services.AddSingleton<HostOriginFilter>();
builder.Services.AddSingleton(_ => SmartSubtitleConverterApi.Storage.ExtensionAllowlist.Shared);
builder.Services.AddSingleton<SmartSubtitleConverterApi.Storage.BrowseQueue>();
builder.Services.AddSingleton<ProcessExit>();
builder.Services.AddSingleton<SelfTest>();
builder.Services.AddSingleton<Metrics>();
builder.Services.AddHostedService<SelfTestService>();
builder.Services.AddSingleton<SmartSubtitleConverterApi.Storage.StorageCheck>();
builder.Services.AddHostedService<SmartSubtitleConverterApi.Storage.StorageCheckService>(); // warning-only, does not gate /readyz
builder.Services.AddOpenApi("v1", o =>
{
    o.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info.Title = "Smart Subtitle Converter API";
        doc.Info.Version = LibseInfo.AppVersion; // the csproj <Version>, same as /healthz
        doc.Info.Description = "Convert subtitles between text and binary broadcast formats (EBU STL, PAC, Cavena 890, ...) with Subtitle Edit's libse. Errors are RFC 7807 problem+json with a stable `code`, a `hint` and the `requestId`. Send `Authorization: Bearer <key>` when API keys are configured.";
        return OpenApiContract.Apply(doc);
    });
});

if (opts.CorsOrigins.Length > 0)
{
    builder.Services.AddCors(c => c.AddDefaultPolicy(p => p.WithOrigins(opts.CorsOrigins).AllowAnyMethod()
        .WithHeaders("Authorization", "Content-Type", "X-Request-Id", "Prefer")
        .WithExposedHeaders("X-Request-Id", "X-Detected-Format", "X-Detected-Encoding", "X-Detected-Code-Page", "X-Output-Kind", "X-Cue-Count", "X-Cue-Count-In",
            "X-Warnings-Count", "X-Warnings", "X-Libse-Version", "X-Profile", "Content-Disposition", "Retry-After", "X-Job-Id", "Location", "Preference-Applied")));
}

if (opts.RateLimitPerMinute > 0)
{
    builder.Services.AddRateLimiter(r =>
    {
        r.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            !ctx.Request.Path.StartsWithSegments("/v1")
                ? RateLimitPartition.GetNoLimiter("exempt")
                : RateLimitPartition.GetFixedWindowLimiter(
                    ctx.Items["keyName"] as string ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = opts.RateLimitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        r.OnRejected = async (c, _) =>
        {
            c.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>().LogWarning("ratelimit.rejected keyName={keyName}", c.HttpContext.Items["keyName"]);
            await Problems.WriteAsync(c.HttpContext, new ApiException(429, ErrorCodes.RateLimited, "Too many requests.",
                $"The limit is {opts.RateLimitPerMinute} requests per minute; retry after Retry-After seconds.") { RetryAfterSeconds = 60 });
        };
    });
}

var app = builder.Build();
if (logFormatInvalid)
    app.Logger.LogWarning("{Line}", $"logging.config warning=invalid-log-format var=LOG_FORMAT value={SmartSubtitleConverterApi.Storage.StorageDiagnostics.Q(logFormatRaw)} "
        + "problem=\"not a supported log format; using text\" fix=\"set LOG_FORMAT=text (plain lines, default) or LOG_FORMAT=json (JSON console)\"");

if (opts.KnownProxies.Length > 0)
{
    var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost };
    fwd.KnownProxies.Clear();
    fwd.KnownIPNetworks.Clear();
    foreach (var p in opts.KnownProxies) if (IPAddress.TryParse(p, out var ip)) fwd.KnownProxies.Add(ip);
    app.UseForwardedHeaders(fwd);
}

app.UseMiddleware<RequestIdMiddleware>();
app.UseMiddleware<ErrorMiddleware>();
app.UseMiddleware<HostOriginMiddleware>();
app.Use(async (ctx, next) =>
{
    // Per-endpoint body caps (/v1/limits maxRequestBodyBytes): JSON carries base64, multipart raw bytes.
    var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (feature is { IsReadOnly: false })
    {
        var json = (ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
                   && (ctx.Request.Path.Equals("/v1/convert/json") || ctx.Request.Path.Equals("/v1/detect"));
        feature.MaxRequestBodySize = json ? opts.MaxJsonBodyBytes : opts.MaxMultipartBodyBytes;
    }
    await next();
});
app.Use(async (ctx, next) =>
{
    await next();
    if (ctx.Items["errorCode"] is string code) ctx.RequestServices.GetRequiredService<Metrics>().Count(code);
});
if (opts.CorsOrigins.Length > 0) app.UseCors();
app.UseMiddleware<ApiKeyMiddleware>();
if (opts.RateLimitPerMinute > 0) app.UseRateLimiter();

app.MapOpenApi("/openapi/{documentName}.json");
if (opts.DocsEnabled)
{
    app.MapScalarApiReference("/docs", s => s.WithTitle("Smart Subtitle Converter API").WithOpenApiRoutePattern("/openapi/{documentName}.json"));
}

var v1 = app.MapGroup("/v1");
ConvertEndpoints.Map(v1);
InfoEndpoints.Map(v1);
StorageEndpoints.Map(v1);
HealthEndpoints.Map(app, opts);
app.MapGet("/", () => Results.Redirect(opts.DocsEnabled ? "/docs" : "/openapi/v1.json")).ExcludeFromDescription();
app.MapFallback((HttpContext ctx) =>
{
    // The JSON body parameter makes routing skip /v1/convert/json for other content types; that's a 415, not a 404.
    if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.Equals("/v1/convert/json", StringComparison.OrdinalIgnoreCase))
        throw new ApiException(415, ErrorCodes.UnsupportedMediaType, $"Content-Type '{ctx.Request.ContentType}' is not supported here.",
            "Send Content-Type: application/json, or use multipart POST /v1/convert.");
    throw new ApiException(404, ErrorCodes.NotFound, $"No endpoint at {ctx.Request.Method} {ctx.Request.Path}.", "See /docs or /openapi/v1.json for the API.");
}).ExcludeFromDescription();

// API keys are required by default (H1): validate, or generate one into an empty key file, before accepting requests.
try { ApiKeyBootstrap.Ensure(opts, app.Logger); }
catch (StartupConfigException ex)
{
    foreach (var line in ("auth.startup-failed The API did not start. " + ex.Message).Split('\n')) app.Logger.LogCritical("{Line}", line);
    await app.DisposeAsync(); // flush the console logger
    return 78; // EX_CONFIG
}
app.Services.GetRequiredService<HostOriginFilter>().Log(app.Logger);

BinaryIO.RegisterEbuHelper();
BinaryIO.SweepAtStartup(app.Logger);
var tempLease = BinaryIO.AcquireRoot();
app.Lifetime.ApplicationStopped.Register(tempLease.Dispose);

app.Lifetime.ApplicationStarted.Register(() =>
{
    var store = app.Services.GetRequiredService<PresetStore>();
    var (valid, invalid) = store.Counts();
    app.Logger.LogInformation("smart-subtitle-converter-api {version} (libse {libse}) started; formats={formats} binaryFormats={binary} presets valid={valid} invalid={invalid} queueCapacity={cap} timeout={timeout}s verboseErrors={verbose}",
        LibseInfo.AppVersion, LibseInfo.Version, app.Services.GetRequiredService<FormatRegistry>().All.Count(f => f.TextBased), app.Services.GetRequiredService<FormatRegistry>().All.Count(f => f.Kind == FormatKinds.Binary && f.CanRead), valid, invalid, opts.QueueCapacity, opts.ConversionTimeoutSeconds, opts.ErrorsVerbose);
    app.Logger.LogInformation("{Line}", opts.ErrorsVerbose
        ? "errors.verbose enabled=true: error responses include stack traces and internals (ERRORS_VERBOSE=true). Turn it off for anything reachable by other people."
        : "errors.verbose enabled=false: error responses carry code, detail, hint and requestId only; stack traces and internals go to this log (error.details lines, same requestId). Set ERRORS_VERBOSE=true only while debugging.");
    app.Logger.LogInformation("{Line}", $"conversion.queue workers=1 (libse settings are process-global) capacity={opts.QueueCapacity} timeout={opts.ConversionTimeoutSeconds}s stuckGrace={opts.EffectiveStuckGraceSeconds}s: "
        + $"a conversion still running {opts.EffectiveStuckGraceSeconds}s after its timeout makes the API exit with code {ConversionQueue.StuckExitCode}; run the container with restart: unless-stopped so it comes back.");
    var allow = app.Services.GetRequiredService<SmartSubtitleConverterApi.Storage.ExtensionAllowlist>();
    app.Logger.LogInformation("{Line}", $"storage.extensions count={allow.Sorted.Count} (file and S3 locations: only these subtitle extensions are listed, read and written; HTTPS URLs and the {{output}}.json sidecar are exempt; full list in GET /v1/limits storage.extensions)");
    app.Logger.LogInformation("{Line}", $"storage.browse concurrency={opts.BrowseConcurrency} queueCapacity={opts.BrowseQueueCapacity} waitSeconds={opts.BrowseWaitSeconds} listMaxEntries={SmartSubtitleConverterApi.Storage.StorageListing.MaxEntries} "
        + "(listings, reads by location and job file reads; beyond the line: 503 browse-busy; BROWSE_CONCURRENCY, BROWSE_QUEUE_CAPACITY, BROWSE_WAIT_SECONDS)");
    app.Logger.LogInformation("{Line}", "storage.hidden names starting with '.' (and anything inside such folders) are never read, listed or written in file and S3 locations (422 hidden-path); HTTPS URLs are not affected. Symlinks are never followed.");
    _ = app.Services.GetRequiredService<ApiKeyStore>();
    app.Logger.LogInformation("{Line}", opts.StorageListEnabled
        ? $"storage.list enabled=true locations={StorageEndpoints.Locations(opts).Count(l => l.Listable)} (browse and read by location on GET /v1/storage/*; HTTPS is never listable)"
        : "storage.list enabled=false (set STORAGE_LIST_ENABLED=true to browse allowed locations and read files by location)");
    var hiddenRoots = opts.StorageFileRoots.Count(r => SmartSubtitleConverterApi.Storage.HiddenPaths.IsHidden(Path.GetFullPath(r))) + opts.S3AllowedPrefixes.Count(SmartSubtitleConverterApi.Storage.HiddenPaths.IsHidden);
    if (hiddenRoots > 0)
        app.Logger.LogWarning("{Line}", $"storage.hidden roots={hiddenRoots}: {hiddenRoots} STORAGE_FILE_ROOTS / S3_ALLOWED_PREFIXES entries have a component starting with '.'; nothing there can be read, listed or written (422 hidden-path), and they are left out of GET /v1/storage/locations. Fix: use folders whose names don't start with '.'.");
});

app.Run();
return 0;

public partial class Program;
