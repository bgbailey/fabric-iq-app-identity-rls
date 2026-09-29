using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SemanticGateway.Identity;

namespace SemanticGateway.Fabric;

public sealed record EmbedInfo(string ReportId, string EmbedUrl, string Token, string Expiration);

/// <summary>
/// Power BI Embedded ("app owns data") for the same user, used to show that the report and the
/// AI path see the same rows. The embed token carries the same effective identity the AI path
/// sends on every query: the same RLS role and the same user key.
/// </summary>
public sealed class EmbedTokenService(PowerBiAppIdentity appIdentity, IOptions<FabricSettings> options)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ReportId);

    public async Task<EmbedInfo> CreateAsync(AppUser user, CancellationToken cancellationToken)
    {
        var fabric = options.Value;
        var accessToken = await appIdentity.GetTokenAsync(cancellationToken);

        var report = await SendAsync(HttpMethod.Get, $"https://api.powerbi.com/v1.0/myorg/groups/{fabric.WorkspaceId}/reports/{fabric.ReportId}", null, accessToken, cancellationToken);

        var identity = new EffectiveIdentity(
            Username: user.UserKey,
            Roles: [fabric.RlsRole],
            CustomData: fabric.IdentityMode == IdentityMode.CustomData ? user.UserKey : null,
            Datasets: [fabric.SemanticModelId]);
        var tokenRequest = new
        {
            datasets = new[] { new { id = fabric.SemanticModelId } },
            reports = new[] { new { id = fabric.ReportId } },
            identities = new[] { identity },
            lifetimeInMinutes = 10
        };
        var token = await SendAsync(HttpMethod.Post, "https://api.powerbi.com/v1.0/myorg/GenerateToken", tokenRequest, accessToken, cancellationToken);

        return new EmbedInfo(fabric.ReportId!, (string)report["embedUrl"]!, (string)token["token"]!, (string)token["expiration"]!);
    }

    private static async Task<JsonNode> SendAsync(HttpMethod method, string url, object? body, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        using var response = await Http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Power BI returned HTTP {(int)response.StatusCode} for {url}: {text}");
        return JsonNode.Parse(text)!;
    }

    // "username" is required by GenerateToken even when RLS reads CUSTOMDATA().
    private sealed record EffectiveIdentity(
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("roles")] string[] Roles,
        [property: JsonPropertyName("customData")] string? CustomData,
        [property: JsonPropertyName("datasets")] string[] Datasets);
}
