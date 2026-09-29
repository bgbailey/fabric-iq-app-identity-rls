using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using IqRls.Core;

namespace IqRls.Demo;

public sealed record IqConfiguration(Guid TenantId, Guid ClientId, string ExpectedPrincipal)
{
    public const string Audience = "https://analysis.windows.net/powerbi/api";
    public static string[] Scopes => ["https://analysis.windows.net/powerbi/api/Item.Read.All",
        "https://analysis.windows.net/powerbi/api/Item.Execute.All",
        "https://analysis.windows.net/powerbi/api/Dataset.Read.All"];

    public static DemoException Prerequisite() => new("iq_auth_required",
        "Fabric IQ requires an explicit MCAPS DEV public client with delegated Power BI Item.Read.All, " +
        "Item.Execute.All and Dataset.Read.All permissions, http://localhost desktop redirect, and an explicit " +
        "--iq-sign-in before live requests. App-only, corporate and Azure CLI IQ credentials are not used.", 503);

    public static IqConfiguration Validate(DemoStartup startup)
    {
        var config = startup.Configuration?.Iq ?? throw Prerequisite();
        if (config.ClientId == Guid.Empty || config.TenantId == Guid.Empty ||
            string.IsNullOrWhiteSpace(config.ExpectedPrincipal) ||
            config.ExpectedPrincipal.EndsWith("@microsoft.com", StringComparison.OrdinalIgnoreCase) ||
            startup.QueryConfiguration?.TenantId != config.TenantId ||
            startup.Configuration!.Llm.TenantId != config.TenantId ||
            !string.Equals(startup.Configuration.Llm.ExpectedPrincipal, config.ExpectedPrincipal, StringComparison.OrdinalIgnoreCase))
            throw new DemoException("iq_identity_configuration", "IQ, query and model credentials must match the externally configured development tenant and user; corporate credentials are not permitted.", 503);
        return config;
    }
}

public static class IqDelegatedSignIn
{
    // Called only by the explicit command-line sign-in lane, never from a request handler.
    public static async Task AuthenticateAsync(DemoStartup startup, CancellationToken cancellationToken)
    {
        var config = IqConfiguration.Validate(startup);
        if (!OperatingSystem.IsWindows()) throw IqConfiguration.Prerequisite();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            using var store = new LocalIqCredentialStore(startup.RepositoryRoot, config);
            using var http = CloudHttp.Create();
            var credential = new InteractiveBrowserCredential(store.Options(config, http, requireRecord: false));
            var record = await credential.AuthenticateAsync(new TokenRequestContext(IqConfiguration.Scopes), timeout.Token)
                .ConfigureAwait(false);
            PinnedIqDelegatedTokens.ValidateRecord(record, config);
            var token = await credential.GetTokenAsync(new TokenRequestContext(IqConfiguration.Scopes), timeout.Token)
                .ConfigureAwait(false);
            PinnedIqDelegatedTokens.ValidateIdentity(token.Token, config, DateTimeOffset.UtcNow);
            await store.SaveRecordAsync(record, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DemoException("iq_sign_in_timeout", "The explicit Fabric IQ sign-in did not complete within five minutes.", 504);
        }
        catch (DemoException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is AuthenticationFailedException or Azure.RequestFailedException or
                                     IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            throw IqConfiguration.Prerequisite();
        }
    }
}

public sealed class PinnedIqDelegatedTokens : IAccessTokenSource, IDisposable
{
    private readonly IqConfiguration configuration;
    private readonly IDemoClock clock;
    private readonly TokenCredential credential;
    private readonly IDisposable? store;
    private readonly HttpClient? http;

    public PinnedIqDelegatedTokens(DemoStartup startup, IDemoClock clock)
    {
        configuration = IqConfiguration.Validate(startup);
        this.clock = clock;
        if (!OperatingSystem.IsWindows()) throw IqConfiguration.Prerequisite();
        LocalIqCredentialStore? opened = null;
        HttpClient? transport = null;
        try
        {
            opened = new LocalIqCredentialStore(startup.RepositoryRoot, configuration);
            transport = CloudHttp.Create();
            credential = new InteractiveBrowserCredential(opened.Options(configuration, transport, requireRecord: true));
            store = opened;
            http = transport;
        }
        catch (Exception error) when (error is DemoException or AuthenticationFailedException or
                                     IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            (opened as IDisposable)?.Dispose();
            transport?.Dispose();
            throw IqConfiguration.Prerequisite();
        }
    }

    public PinnedIqDelegatedTokens(IqConfiguration configuration, IDemoClock clock, TokenCredential credential)
    {
        this.configuration = configuration;
        this.clock = clock;
        this.credential = credential;
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(IqConfiguration.Scopes), deadline.Token)
                .ConfigureAwait(false);
            ValidateIdentity(token.Token, configuration, clock.UtcNow);
            return token.Token;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DemoException("iq_auth_timeout", "Silent Fabric IQ authentication timed out. No interactive sign-in was started.", 504);
        }
        catch (OperationCanceledException) { throw; }
        catch (DemoException) { throw; }
        catch (Exception error) when (error is AuthenticationFailedException or Azure.RequestFailedException or
                                     IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            throw IqConfiguration.Prerequisite();
        }
    }

    public static void ValidateIdentity(string token, IqConfiguration config, DateTimeOffset now)
    {
        try
        {
            if (token.Length > 32768 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && !"-_.".Contains(c)))
                throw new FormatException();
            var parts = token.Split('.');
            if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace)) throw new FormatException();
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            var root = document.RootElement;
            var claims = root.EnumerateObject().ToArray();
            if (claims.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != claims.Length)
                throw new FormatException();
            if (root.GetProperty("tid").GetString() != config.TenantId.ToString("D") ||
                root.GetProperty("exp").GetInt64() <= now.AddSeconds(30).ToUnixTimeSeconds() ||
                (root.TryGetProperty("nbf", out var nbf) && nbf.GetInt64() > now.ToUnixTimeSeconds()) ||
                (root.TryGetProperty("idtyp", out var idtyp) && idtyp.GetString() != "user"))
                throw new FormatException();
            var audience = root.GetProperty("aud").GetString();
            if (audience is not IqConfiguration.Audience and not "00000009-0000-0000-c000-000000000000")
                throw new FormatException();
            var clients = new[] { "appid", "azp" }.Where(n => root.TryGetProperty(n, out _))
                .Select(n => root.GetProperty(n).GetString()).ToArray();
            if (clients.Length == 0 || clients.Any(c => c != config.ClientId.ToString("D"))) throw new FormatException();
            var principals = new[] { "upn", "preferred_username", "unique_name" }
                .Where(n => root.TryGetProperty(n, out _)).Select(n => root.GetProperty(n).GetString()).ToArray();
            if (principals.Length == 0 || principals.Any(p => !string.Equals(p, config.ExpectedPrincipal, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException();
            var scopes = root.GetProperty("scp").GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (new[] { "Item.Read.All", "Item.Execute.All", "Dataset.Read.All" }.Any(s => !scopes.Contains(s, StringComparer.Ordinal)))
                throw new FormatException();
            // Mix-up guard only: Entra issues the token; Fabric IQ validates its signature and permissions.
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or
                                     InvalidOperationException or OverflowException or NullReferenceException)
        {
            throw new DemoException("iq_identity_rejected", "Fabric IQ requires the configured delegated user, client, tenant, Power BI audience and all three delegated scopes.", 502);
        }
    }

    public static void ValidateRecord(AuthenticationRecord record, IqConfiguration config)
    {
        // MSAL may persist the public-cloud preferred-cache alias rather than the network host.
        string[] publicCloudAliases = ["login.microsoftonline.com", "login.windows.net", "login.microsoft.com", "sts.windows.net"];
        if (record.TenantId != config.TenantId.ToString("D") || record.ClientId != config.ClientId.ToString("D") ||
            !string.Equals(record.Username, config.ExpectedPrincipal, StringComparison.OrdinalIgnoreCase) ||
            !publicCloudAliases.Contains(record.Authority, StringComparer.OrdinalIgnoreCase))
            throw IqConfiguration.Prerequisite();
    }

    public void Dispose()
    {
        store?.Dispose();
        http?.Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class LocalIqCredentialStore : UnsafeTokenCacheOptions, IDisposable
{
    private const int MaxCacheBytes = 1024 * 1024;
    private readonly string directory;
    private readonly string repositoryRoot;
    private readonly FileStream lease;

    internal LocalIqCredentialStore(string repositoryRoot, IqConfiguration config)
    {
        this.repositoryRoot = repositoryRoot;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        directory = DemoStartup.ExternalPath(Path.Combine(local, "scout", "auth", "fabric-iq",
            config.TenantId.ToString("D"), config.ClientId.ToString("D")), repositoryRoot);
        Directory.CreateDirectory(directory);
        lease = new FileStream(SafePath("credential.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    internal InteractiveBrowserCredentialOptions Options(IqConfiguration config, HttpClient http, bool requireRecord)
    {
        AuthenticationRecord? record = null;
        if (requireRecord)
        {
            using var stream = new MemoryStream(ReadProtected("account.bin"));
            record = AuthenticationRecord.Deserialize(stream);
            PinnedIqDelegatedTokens.ValidateRecord(record, config);
        }
        return new InteractiveBrowserCredentialOptions
        {
            TenantId = config.TenantId.ToString("D"),
            ClientId = config.ClientId.ToString("D"),
            LoginHint = config.ExpectedPrincipal,
            RedirectUri = new Uri("http://localhost"),
            AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
            DisableAutomaticAuthentication = true,
            AuthenticationRecord = record,
            TokenCachePersistenceOptions = this,
            Transport = new HttpClientTransport(http),
            Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(30) },
            Diagnostics = { IsLoggingEnabled = false, IsLoggingContentEnabled = false },
            IsUnsafeSupportLoggingEnabled = false
        };
    }

    internal async Task SaveRecordAsync(AuthenticationRecord record, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await record.SerializeAsync(stream, cancellationToken).ConfigureAwait(false);
        WriteProtected("account.bin", stream.ToArray());
    }

    protected override Task<ReadOnlyMemory<byte>> RefreshCacheAsync() =>
        Task.FromResult<ReadOnlyMemory<byte>>(ReadProtected("tokens.bin", optional: true));

    protected override Task TokenCacheUpdatedAsync(TokenCacheUpdatedArgs args)
    {
        if (args.IsCaeEnabled) throw IqConfiguration.Prerequisite();
        WriteProtected("tokens.bin", args.UnsafeCacheData.ToArray());
        return Task.CompletedTask;
    }

    private string SafePath(string name) => DemoStartup.ExternalPath(Path.Combine(directory, name), repositoryRoot);

    private byte[] ReadProtected(string name, bool optional = false)
    {
        var path = SafePath(name);
        if (optional && !File.Exists(path)) return [];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaxCacheBytes) throw IqConfiguration.Prerequisite();
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
    }

    private void WriteProtected(string name, byte[] bytes)
    {
        try
        {
            if (bytes.Length is <= 0 or > MaxCacheBytes - 4096) throw IqConfiguration.Prerequisite();
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            var pending = SafePath(name + ".pending");
            try
            {
                using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(encrypted);
                    file.Flush(flushToDisk: true);
                }
                File.Move(pending, SafePath(name), overwrite: true);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose() => lease.Dispose();
}
