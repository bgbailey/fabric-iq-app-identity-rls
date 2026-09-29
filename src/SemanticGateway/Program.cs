using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore.Authentication;
using SemanticGateway;
using SemanticGateway.Chat;
using SemanticGateway.Fabric;
using SemanticGateway.Identity;
using SemanticGateway.Tools;

var builder = WebApplication.CreateBuilder(args);

// Your tenant, workspace, model and app IDs go in appsettings.Local.json (not committed).
// Environment variables and command-line arguments still override it.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables().AddCommandLine(args);
builder.Services.Configure<FabricSettings>(builder.Configuration.GetSection("Fabric"));
builder.Services.Configure<FabricIqSettings>(builder.Configuration.GetSection("FabricIq"));
builder.Services.Configure<AzureOpenAISettings>(builder.Configuration.GetSection("AzureOpenAI"));
builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection("Auth"));
var auth = builder.Configuration.GetSection("Auth").Get<AuthSettings>() ?? new AuthSettings();

// One-time sign-in of the Fabric IQ metadata account:  dotnet run -- sign-in
if (args.Contains("sign-in"))
{
    await FabricIqSchemaClient.SignInAsync(
        builder.Configuration.GetSection("FabricIq").Get<FabricIqSettings>()!,
        builder.Configuration.GetSection("Fabric").Get<FabricSettings>()!);
    return;
}

builder.Services.AddSingleton<AppUserDirectory>();
builder.Services.AddSingleton<DevIdentityProvider>();
builder.Services.AddSingleton<PowerBiAppIdentity>();
builder.Services.AddSingleton<DaxQueryClient>();
builder.Services.AddSingleton<FabricIqSchemaClient>();
builder.Services.AddSingleton<SchemaCache>();
builder.Services.AddSingleton<EmbedTokenService>();
builder.Services.AddSingleton<SemanticModelTools>();
builder.Services.AddSingleton<ChatAgent>();

// 1. Authenticate the ISV's own users. Only tokens issued for this gateway (audience) are accepted.
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters { ValidIssuer = auth.Issuer, ValidAudience = auth.Audience };
        if (auth.IsDevelopment) options.RequireHttpsMetadata = false;
        else options.Authority = auth.Authority;
    })
    // 2. Tell MCP clients where to get a token (OAuth protected resource metadata, RFC 9728).
    .AddMcp(options => options.ResourceMetadata = new()
    {
        Resource = auth.PublicUrl + "/mcp",
        AuthorizationServers = [auth.IsDevelopment ? auth.Issuer : auth.Authority!],
        ResourceName = "ISV semantic gateway"
    });
if (auth.IsDevelopment)
{
    // The demo issuer signs tokens with an in-memory key; trust exactly that key.
    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<DevIdentityProvider>((options, issuer) => options.TokenValidationParameters.IssuerSigningKey = issuer.SigningKey);
}
builder.Services.AddAuthorization();

// 3. The MCP server: the same three tools the portal agent uses.
builder.Services.AddSemanticModelMcpServer();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (UnauthorizedAccessException error) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "forbidden", message = error.Message });
    }
});

if (auth.IsDevelopment) app.MapDevIdentityProvider();

app.MapMcp("/mcp").RequireAuthorization();

app.MapGet("/api/config", (AppUserDirectory users, EmbedTokenService embed, IOptions<AzureOpenAISettings> ai, IOptions<FabricSettings> fabric) => new
{
    model = ai.Value.Deployment,
    identityMode = fabric.Value.IdentityMode.ToString(),
    rlsRole = fabric.Value.RlsRole,
    embedEnabled = embed.IsConfigured,
    auth = new { mode = auth.Mode, users = auth.IsDevelopment ? users.All.Select(u => new { username = u.Subject, u.DisplayName }) : null },
    suggestions = new[]
    {
        "What is my total amount and activity count?",
        "Break down my activity by customer and product.",
        "Show the activity records behind my total.",
        "Compare 1 January and 2 January 2026.",
        "What is Customer B's Home activity?"
    }
});

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/me", (ClaimsPrincipal principal, AppUserDirectory users, IOptions<FabricSettings> fabric) =>
{
    var user = users.Resolve(principal);
    return new { user.Subject, user.DisplayName, user.UserKey, identityMode = fabric.Value.IdentityMode.ToString(), role = fabric.Value.RlsRole };
});

// The portal chat streams its progress as newline-delimited JSON so the UI can show each step.
api.MapPost("/chat", async (ChatRequest chat, HttpContext http, AppUserDirectory users, ChatAgent agent) =>
{
    var user = users.Resolve(http.User);
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    http.Response.ContentType = "application/x-ndjson";
    async Task Write(object frame)
    {
        await http.Response.WriteAsync(JsonSerializer.Serialize(frame, json) + "\n", http.RequestAborted);
        await http.Response.Body.FlushAsync(http.RequestAborted);
    }

    try
    {
        var result = await agent.RunAsync(user, chat, chatEvent => Write(new { type = "event", @event = chatEvent }), http.RequestAborted);
        await Write(new { type = "result", result });
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        await Write(new { type = "error", message = error.Message });
    }
});

// Power BI Embedded for the same user, and the same numbers through the gateway, for comparison.
api.MapGet("/embed", async (ClaimsPrincipal principal, AppUserDirectory users, EmbedTokenService embed, CancellationToken ct) =>
    await embed.CreateAsync(users.Resolve(principal), ct));

api.MapGet("/parity", async (ClaimsPrincipal principal, AppUserDirectory users, DaxQueryClient dax, CancellationToken ct) =>
    await dax.ExecuteAsync(users.Resolve(principal),
        "EVALUATE SUMMARIZECOLUMNS('Scope'[Customer], 'Scope'[Product], \"Total Amount\", [Total Amount], \"Activity Count\", [Activity Count]) ORDER BY 'Scope'[Customer], 'Scope'[Product]",
        ct));

app.MapFallbackToFile("index.html");

app.Run();
