using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IqRls.Core;

namespace IqRls.Demo;

public sealed class DemoException(string code, string safeMessage, int status = 503) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public static DemoException Configuration() =>
        new("invalid_configuration", "The local demo configuration or startup arguments are invalid.");
}

public sealed record LlmConfiguration(Uri Endpoint, string Deployment, Guid TenantId, Guid SubscriptionId, string ExpectedPrincipal);

public sealed record DemoConfiguration(bool LiveEnabled, DateTimeOffset LiveUntilUtc, int MaxCalls,
    string QueryConfigPath, LlmConfiguration Llm, IqConfiguration? Iq = null)
{
    public static DemoConfiguration Parse(string json)
    {
        try
        {
            if (json.Length > 16384) throw DemoException.Configuration();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("iq", out var iq)) throw IqConfiguration.Prerequisite();
            JsonShape.Exact(root, "liveEnabled", "liveUntilUtc", "maxCalls", "queryConfigPath", "llm", "iq");
            var live = root.GetProperty("liveEnabled").GetBoolean();
            var expiryText = JsonShape.Text(root, "liveUntilUtc");
            if (!expiryText.EndsWith('Z') ||
                !DateTimeOffset.TryParse(expiryText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry))
                throw DemoException.Configuration();
            var calls = root.GetProperty("maxCalls").GetInt32();
            if (calls is < 1 or > 100) throw DemoException.Configuration();
            var llm = root.GetProperty("llm");
            JsonShape.Exact(llm, "endpoint", "deployment", "tenantId", "subscriptionId", "expectedPrincipal");
            var endpointText = JsonShape.Text(llm, "endpoint");
            if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ||
                endpoint.Scheme != "https" || endpoint.Port != 443 ||
                !Regex.IsMatch(endpoint.Host, @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.openai\.azure\.com\z",
                    RegexOptions.CultureInvariant) ||
                endpoint.AbsolutePath != "/" || endpoint.UserInfo != "" || endpoint.Query != "" || endpoint.Fragment != "" ||
                endpointText.Contains('\\'))
                throw DemoException.Configuration();
            var deployment = JsonShape.Text(llm, "deployment");
            if (!Regex.IsMatch(deployment, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant))
                throw DemoException.Configuration();
            var principal = JsonShape.Text(llm, "expectedPrincipal");
            if (principal.Length > 254 || principal.Count(c => c == '@') != 1 ||
                principal.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) || principal.StartsWith('@') || principal.EndsWith('@'))
                throw DemoException.Configuration();
            JsonShape.Exact(iq, "tenantId", "clientId", "expectedPrincipal");
            var iqPrincipal = JsonShape.Text(iq, "expectedPrincipal");
            if (iqPrincipal.Length > 254 || iqPrincipal.Count(c => c == '@') != 1 ||
                iqPrincipal.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
                throw DemoException.Configuration();
            var iqConfiguration = new IqConfiguration(JsonShape.Id(iq, "tenantId"), JsonShape.Id(iq, "clientId"), iqPrincipal);
            if (iqConfiguration.TenantId != JsonShape.Id(llm, "tenantId")) throw DemoException.Configuration();
            return new(live, expiry, calls, JsonShape.Text(root, "queryConfigPath"),
                new(endpoint, deployment, JsonShape.Id(llm, "tenantId"), JsonShape.Id(llm, "subscriptionId"), principal),
                iqConfiguration);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw DemoException.Configuration();
        }
    }
}

public static class JsonShape
{
    public static void Exact(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw DemoException.Configuration();
        var actual = element.EnumerateObject().Select(p => p.Name).ToArray();
        if (actual.Length != names.Length || actual.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            actual.Any(n => !names.Contains(n, StringComparer.Ordinal)))
            throw DemoException.Configuration();
    }

    public static string Text(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.String &&
        element.GetProperty(name).GetString() is { Length: > 0 } text ? text : throw DemoException.Configuration();

    public static Guid Id(JsonElement element, string name) =>
        Guid.TryParseExact(Text(element, name), "D", out var id) && id != Guid.Empty ? id : throw DemoException.Configuration();
}

public sealed record DemoStartup(string RepositoryRoot, DemoConfiguration? Configuration,
    LiveConfiguration? QueryConfiguration, string? BudgetPath, string? BudgetKey, bool AllowLive)
{
    public const string Address = "http://127.0.0.1:5187";
    public bool Enabled => AllowLive && Configuration?.LiveEnabled == true;

    public static DemoStartup Read(string[] args, string repositoryRoot)
    {
        string[] overrides = ["ASPNETCORE_URLS", "DOTNET_URLS", "URLS", "ASPNETCORE_HTTP_PORTS",
            "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS"];
        if (overrides.Any(n => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n))) ||
            Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(n => n.Contains("Kestrel__", StringComparison.OrdinalIgnoreCase)))
            throw DemoException.Configuration();
        string? configPath = null;
        var allowLive = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--allow-live" when !allowLive:
                    allowLive = true;
                    break;
                case "--demo-config" when configPath is null && i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                default:
                    throw DemoException.Configuration();
            }
        }
        if (configPath is null)
        {
            if (allowLive) throw DemoException.Configuration();
            return new(repositoryRoot, null, null, null, null, false);
        }
        try
        {
            configPath = ExternalPath(configPath, repositoryRoot);
            var configText = ReadBounded(configPath);
            var config = DemoConfiguration.Parse(configText);
            var queryPath = ExternalPath(config.QueryConfigPath, repositoryRoot);
            var queryText = ReadBounded(queryPath);
            var query = LiveConfiguration.Parse(queryText);
            if (query.TenantId != config.Llm.TenantId || query.TenantId != config.Iq!.TenantId)
                throw DemoException.Configuration();
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(configText + "\n" + queryText)));
            return new(repositoryRoot, config, query, ExternalPath(configPath + ".budget.json", repositoryRoot), key, allowLive);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
                                     NotSupportedException or HarnessException)
        {
            throw DemoException.Configuration();
        }
    }

    private static string ReadBounded(string path)
    {
        if (new FileInfo(path).Length > 32768) throw DemoException.Configuration();
        return File.ReadAllText(path);
    }

    public static string ExternalPath(string path, string repositoryRoot)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw DemoException.Configuration();
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) ||
            full.AsSpan(Path.GetPathRoot(full)!.Length).Contains(':'))
            throw DemoException.Configuration();
        for (var check = full; !string.IsNullOrEmpty(check); check = Path.GetDirectoryName(check))
            if ((File.Exists(check) || Directory.Exists(check)) &&
                (File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0)
                throw DemoException.Configuration();
        return full;
    }
}
