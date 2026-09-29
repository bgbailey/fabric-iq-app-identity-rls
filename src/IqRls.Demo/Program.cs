using System.Net;
using System.Text;
using System.Text.Json;
using IqRls.Demo;
using Microsoft.Extensions.FileProviders;

public partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Contains("--iq-schema-probe", StringComparer.Ordinal))
            {
                if (args.Length != 6 || args[0] != "--iq-schema-probe" ||
                    args[1] != "--demo-config" || args[3] != "--out" || args[5] != "--allow-live")
                    throw DemoException.Configuration();
                var startup = DemoApplication.ReadStartup(["--demo-config", args[2], "--allow-live"]);
                var destination = DemoStartup.ExternalPath(args[4], startup.RepositoryRoot);
                if (File.Exists(destination)) throw DemoException.Configuration();
                var clock = new DemoClock();
                using var budget = new LiveBudget(startup, clock);
                budget.EnsureAvailable();
                using var deadline = new CancellationTokenSource(
                    startup.Configuration!.LiveUntilUtc - clock.UtcNow < TimeSpan.FromMinutes(4)
                        ? startup.Configuration.LiveUntilUtc - clock.UtcNow : TimeSpan.FromMinutes(4));
                using var tokens = new PinnedIqDelegatedTokens(startup, clock);
                using var http = CloudHttp.Create();
                var client = new FabricIqSchemaClient(http, budget, result =>
                {
                    using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    JsonSerializer.Serialize(file, result);
                });
                await client.ReadSchemaAsync(startup.QueryConfiguration!.DatasetId,
                    await tokens.GetTokenAsync(deadline.Token),
                    new ExecutionSession(Guid.NewGuid().ToString("N")), deadline.Token);
                Console.WriteLine("iq_schema_probe_complete: Real schema captured to the explicit local output; no DAX query or inference ran.");
                return 0;
            }
            if (args.Contains("--iq-sign-in", StringComparer.Ordinal))
            {
                if (args.Length != 3 || args.Count(a => a == "--iq-sign-in") != 1 ||
                    args[0] != "--iq-sign-in" || args[1] != "--demo-config")
                    throw DemoException.Configuration();
                var startup = DemoApplication.ReadStartup(["--demo-config", args[2]]);
                await IqDelegatedSignIn.AuthenticateAsync(startup, CancellationToken.None);
                Console.WriteLine("iq_sign_in_complete: Delegated IQ authentication is ready in the local protected cache. No demo query was run.");
                return 0;
            }
            var app = DemoApplication.Build(args);
            await app.RunAsync();
            return 0;
        }
        catch (DemoException error)
        {
            Console.Error.WriteLine($"{error.Code}: {error.Message}");
            return 2;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            Console.Error.WriteLine("demo_startup_failed: The local demo could not start.");
            return 2;
        }
    }
}

namespace IqRls.Demo
{
    public static class DemoApplication
    {
        public static DemoStartup ReadStartup(string[] args) => DemoStartup.Read(args, FindRepository());

        public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configureBuilder = null)
        {
            var root = FindRepository();
            var startup = DemoStartup.Read(args, root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = [],
                ContentRootPath = Path.Combine(root, "src", "IqRls.Demo")
            });
            // No appsettings/environment endpoint configuration, SDK logging, request-body logging or URL overrides.
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls(DemoStartup.Address);
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 5187);
                options.Limits.MaxRequestBodySize = 1024;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            });
            builder.Services.AddSingleton(startup);
            builder.Services.AddSingleton<IDemoClock, DemoClock>();
            builder.Services.AddSingleton<ILiveBudget, LiveBudget>();
            builder.Services.AddSingleton<IDemoQuery, CertificateDemoQuery>();
            builder.Services.AddSingleton<IDemoExplainer, LiveDemoExplainer>();
            builder.Services.AddSingleton<IDemoPlanner, LiveDemoPlanner>();
            builder.Services.AddSingleton<DemoRunner>();
            configureBuilder?.Invoke(builder);
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                context.TraceIdentifier = Guid.NewGuid().ToString("N");
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
                try
                {
                    LocalRequestPolicy.Validate(context);
                    await next(context);
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    if (context.Items["execution"] is ExecutionSession execution) execution.RecordDisconnect();
                    if (context.Response.HasStarted) context.Abort();
                    else context.Response.StatusCode = 499;
                }
                catch (DemoException error)
                {
                    await WriteError(context, error.Code, error.Message, error.Status);
                }
                catch (BadHttpRequestException)
                {
                    await WriteError(context, "invalid_request", "The request is malformed or too large.", 400);
                }
                catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
                {
                    await WriteError(context, "demo_failed", "The demo request failed. No answer was retained.", 500);
                }
            });
            app.MapGet("/api/health", () => Results.Json(new { status = "ok", mode = "technical-proof", cloudChecked = false }));
            app.MapGet("/api/demo", (ILiveBudget budget) => Results.Json(new
            {
                mode = "technical-proof",
                executionMode = "fabric-iq-generated-dax",
                liveEnabled = budget.LiveEnabled,
                liveUntilUtc = budget.LiveUntilUtc,
                llmModel = startup.Configuration?.Llm.Deployment,
                identityNotice = DemoCatalog.IdentityNotice,
                identities = DemoCatalog.Identities,
                questions = DemoCatalog.Questions,
                evidenceNote = DemoCatalog.EvidenceNote
            }));
            app.MapPost("/api/ask", async (HttpContext context, DemoRunner runner) =>
            {
                var request = context.Request;
                if (request.Headers["X-Demo-Request"].Count != 1 || request.Headers["X-Demo-Request"] != "1")
                    throw new DemoException("invalid_request", "The demo request header is required.", 400);
                if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
                    !string.Equals(mediaType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
                    mediaType.Parameters.Any(p => !string.Equals(p.Name, "charset", StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(p.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)) ||
                    request.Headers.ContentEncoding.Count != 0 || request.ContentLength > 1024)
                    throw new DemoException("invalid_request", "Send a bounded UTF-8 application/json request.", 400);
                using var body = new MemoryStream();
                var chunk = new byte[1025];
                while (true)
                {
                    var count = await request.Body.ReadAsync(chunk, context.RequestAborted);
                    if (count == 0) break;
                    if (body.Length + count > 1024)
                        throw new DemoException("invalid_request", "The request is too large.", 400);
                    body.Write(chunk, 0, count);
                }
                string json;
                try { json = new UTF8Encoding(false, true).GetString(body.ToArray()); }
                catch (DecoderFallbackException)
                {
                    throw new DemoException("invalid_request", "Send valid UTF-8 JSON.", 400);
                }
                var input = AskInput.Parse(json);
                var streaming = request.GetTypedHeaders().Accept?.Any(value =>
                    value.MediaType.Value == "application/x-ndjson" && value.Quality != 0) == true;
                Func<ExecutionEvent, CancellationToken, ValueTask>? sink = streaming
                    ? (observation, token) => WriteFrame(context, new { type = "event", @event = observation }, token)
                    : null;
                var execution = new ExecutionSession(context.TraceIdentifier, sink);
                context.Items["execution"] = execution;
                context.Items["streaming"] = streaming;
                var answer = await runner.AskAsync(input, context.TraceIdentifier, context.RequestAborted, execution);
                if (streaming)
                {
                    await WriteFrame(context, new { type = "result", result = answer }, context.RequestAborted);
                    return Results.Empty;
                }
                return Results.Json(answer);
            });
            var dist = Path.Combine(root, "src", "DemoWeb", "dist");
            if (!Directory.Exists(dist)) dist = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (File.Exists(Path.Combine(dist, "index.html")))
            {
                var files = new PhysicalFileProvider(dist);
                app.Lifetime.ApplicationStopped.Register(files.Dispose);
                app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
                app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
                app.MapFallback((HttpContext context) =>
                    context.Request.Path.StartsWithSegments("/api")
                        ? Results.Json(new { error = "not_found", message = "Unknown demo API.", requestId = context.TraceIdentifier }, statusCode: 404)
                        : Results.File(Path.Combine(dist, "index.html"), "text/html"));
            }
            else
            {
                app.MapGet("/", () => Results.Text(
                    "The demo frontend has not been built. Build src\\DemoWeb, then restart this localhost host. " +
                    "The offline catalog is available at /api/demo; no live query or inference has run.",
                    "text/plain", statusCode: 503));
                app.MapFallback((HttpContext context) => Results.Json(new
                {
                    error = "not_found", message = "Unknown demo route.", requestId = context.TraceIdentifier
                }, statusCode: 404));
            }
            return app;
        }

        private static async Task WriteError(HttpContext context, string code, string message, int status)
        {
            if (context.Items["execution"] is ExecutionSession execution)
                await execution.FailAsync(code, context.RequestAborted);
            if (context.Items["streaming"] is true)
            {
                await WriteFrame(context, new { type = "error", error = code, message, requestId = context.TraceIdentifier },
                    context.RequestAborted);
                return;
            }
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new { error = code, message, requestId = context.TraceIdentifier },
                context.RequestAborted);
        }

        private static async ValueTask WriteFrame(HttpContext context, object frame, CancellationToken cancellationToken)
        {
            if (!context.Response.HasStarted) context.Response.ContentType = "application/x-ndjson; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(frame, StreamJsonOptions) + "\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }

        private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

        private static string FindRepository()
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
                for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                    if (File.Exists(Path.Combine(directory.FullName, "IqRls.sln"))) return directory.FullName;
            throw DemoException.Configuration();
        }
    }

    public static class LocalRequestPolicy
    {
        public static void Validate(HttpContext context)
        {
            var request = context.Request;
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote) || request.Scheme != "http" ||
                request.Host.Host != "127.0.0.1" || request.Host.Port != 5187)
                throw new DemoException("local_only", "Use the demo directly at http://127.0.0.1:5187.", 403);
            if (request.Headers.TryGetValue("Origin", out var origin) &&
                (origin.Count != 1 || origin[0] != DemoStartup.Address))
                throw new DemoException("origin_rejected", "Cross-origin browser requests are not allowed.", 403);
            if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site) &&
                site != "same-origin" && site != "none")
                throw new DemoException("origin_rejected", "Cross-origin browser requests are not allowed.", 403);
        }
    }
}
