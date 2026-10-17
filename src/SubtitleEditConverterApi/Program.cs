using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Scalar.AspNetCore;
using SubtitleEditConverterApi.Conversion;
using SubtitleEditConverterApi.Endpoints;
using SubtitleEditConverterApi.Formats;
using SubtitleEditConverterApi.Infrastructure;
using SubtitleEditConverterApi.Profiles;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);
var opts = ApiOptions.FromConfiguration(builder.Configuration);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = opts.MaxUploadBytes + 256 * 1024; // multipart framing + small fields
    k.AddServerHeader = false;
});
builder.Services.Configure<FormOptions>(f =>
{
    f.MultipartBodyLengthLimit = opts.MaxUploadBytes + 256 * 1024;
    f.ValueLengthLimit = opts.ProfileMaxBytes;
    f.ValueCountLimit = 16;
});
builder.Services.ConfigureHttpJsonOptions(j =>
{
    j.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    j.SerializerOptions.DefaultIgnoreCondition = Json.Options.DefaultIgnoreCondition;
});

builder.Services.AddSingleton(opts);
builder.Services.AddSingleton<FormatRegistry>();
builder.Services.AddSingleton<PresetStore>();
builder.Services.AddSingleton<ProfileResolver>();
builder.Services.AddSingleton<ConversionEngine>();
builder.Services.AddSingleton<ConversionQueue>();
builder.Services.AddSingleton<ApiKeyStore>();
builder.Services.AddSingleton<SelfTest>();
builder.Services.AddSingleton<Metrics>();
builder.Services.AddHostedService<SelfTestService>();
builder.Services.AddOpenApi("v1", o =>
{
    o.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info.Title = "Subtitle Edit Converter API";
        doc.Info.Version = "1.0.0";
        doc.Info.Description = "Convert subtitles between text formats with Subtitle Edit's libse. Errors are RFC 7807 problem+json with a stable `code`, a `hint` and the `requestId`. Send `Authorization: Bearer <key>` when API keys are configured.";
        return Task.CompletedTask;
    });
});

if (opts.CorsOrigins.Length > 0)
{
    builder.Services.AddCors(c => c.AddDefaultPolicy(p => p.WithOrigins(opts.CorsOrigins).AllowAnyMethod()
        .WithHeaders("Authorization", "Content-Type", "X-Request-Id")
        .WithExposedHeaders("X-Request-Id", "X-Detected-Format", "X-Detected-Encoding", "X-Cue-Count", "X-Cue-Count-In",
            "X-Warnings-Count", "X-Warnings", "X-Libse-Version", "X-Profile", "Content-Disposition", "Retry-After")));
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
    app.MapScalarApiReference("/docs", s => s.WithTitle("Subtitle Edit Converter API").WithOpenApiRoutePattern("/openapi/{documentName}.json"));
}

var v1 = app.MapGroup("/v1");
ConvertEndpoints.Map(v1);
InfoEndpoints.Map(v1);
HealthEndpoints.Map(app, opts);
app.MapGet("/", () => Results.Redirect(opts.DocsEnabled ? "/docs" : "/openapi/v1.json")).ExcludeFromDescription();
app.MapFallback((HttpContext ctx) =>
{
    throw new ApiException(404, ErrorCodes.NotFound, $"No endpoint at {ctx.Request.Method} {ctx.Request.Path}.", "See /docs or /openapi/v1.json for the API.");
}).ExcludeFromDescription();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var store = app.Services.GetRequiredService<PresetStore>();
    var (valid, invalid) = store.Counts();
    app.Logger.LogInformation("subtitleedit-converter-api {version} (libse {libse}) started; formats={formats} presets valid={valid} invalid={invalid} queueCapacity={cap} timeout={timeout}s verboseErrors={verbose}",
        LibseInfo.AppVersion, LibseInfo.Version, app.Services.GetRequiredService<FormatRegistry>().All.Count(f => f.TextBased), valid, invalid, opts.QueueCapacity, opts.ConversionTimeoutSeconds, opts.ErrorsVerbose);
    _ = app.Services.GetRequiredService<ApiKeyStore>();
});

app.Run();

public partial class Program;
