using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace SemanticGateway.Fabric;

/// <summary>
/// Reads the semantic model schema from the Fabric IQ MCP server, as an MCP client.
///
/// Fabric IQ authenticates delegated Entra users only, so the gateway signs in one dedicated
/// "metadata" account once (<c>dotnet run -- sign-in</c>) and reuses its cached refresh token.
/// Only GetSemanticModelSchema is called; IQ ExecuteQuery is never used, because it would run
/// queries as that account instead of as the ISV's end user.
/// </summary>
public sealed class FabricIqSchemaClient(IOptions<FabricIqSettings> iqOptions, IOptions<FabricSettings> fabricOptions, ILoggerFactory loggers)
{
    private static readonly string[] Scopes =
    [
        "https://analysis.windows.net/powerbi/api/Item.Read.All",
        "https://analysis.windows.net/powerbi/api/Item.Execute.All",
        "https://analysis.windows.net/powerbi/api/Dataset.Read.All"
    ];

    private static string RecordPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "isv-semantic-gateway", "fabric-iq-account.json");

    /// <summary>Returns the schema JSON document that Fabric IQ produces for the configured model.</summary>
    public async Task<string> GetSchemaJsonAsync(CancellationToken cancellationToken)
    {
        var credential = CreateCredential(iqOptions.Value, fabricOptions.Value, LoadRecord());
        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        }
        catch (AuthenticationRequiredException)
        {
            throw new InvalidOperationException("The Fabric IQ metadata account is not signed in. Run: dotnet run -- sign-in");
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(iqOptions.Value.Endpoint),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {token.Token}",
                ["X-Variants"] = "Fabric.Routing.FabricIQ.V1"
            }
        }, loggers);

        await using var client = await McpClient.CreateAsync(transport,
            new McpClientOptions { ClientInfo = new Implementation { Name = "isv-semantic-gateway", Version = "1.0.0" } },
            loggers, cancellationToken);

        var result = await client.CallToolAsync("GetSemanticModelSchema",
            new Dictionary<string, object?> { ["artifactId"] = fabricOptions.Value.SemanticModelId },
            cancellationToken: cancellationToken);

        var text = string.Join("", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return result.IsError == true
            ? throw new InvalidOperationException("Fabric IQ returned an error: " + text)
            : text;
    }

    /// <summary>One-time interactive sign-in of the metadata account. Tokens stay in the OS-protected MSAL cache.</summary>
    public static async Task SignInAsync(FabricIqSettings iq, FabricSettings fabric)
    {
        var credential = CreateCredential(iq, fabric, record: null, interactive: true);
        var record = await credential.AuthenticateAsync(new TokenRequestContext(Scopes));
        Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
        await using var file = File.Create(RecordPath);
        await record.SerializeAsync(file);
        Console.WriteLine($"Signed in {record.Username} for Fabric IQ schema access.");
    }

    private static InteractiveBrowserCredential CreateCredential(FabricIqSettings iq, FabricSettings fabric, AuthenticationRecord? record, bool interactive = false) =>
        new(new InteractiveBrowserCredentialOptions
        {
            TenantId = fabric.TenantId,
            ClientId = iq.ClientId,
            LoginHint = iq.LoginHint,
            RedirectUri = new Uri("http://localhost"),
            AuthenticationRecord = record,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = "isv-semantic-gateway-fabric-iq" },
            // The web server must never open a browser; only the sign-in command may.
            DisableAutomaticAuthentication = !interactive
        });

    private static AuthenticationRecord? LoadRecord()
    {
        if (!File.Exists(RecordPath)) return null;
        using var file = File.OpenRead(RecordPath);
        return AuthenticationRecord.Deserialize(file);
    }
}
