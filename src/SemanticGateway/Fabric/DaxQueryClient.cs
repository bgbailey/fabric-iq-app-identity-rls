using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SemanticGateway.Identity;

namespace SemanticGateway.Fabric;

/// <summary>
/// Runs DAX against the semantic model as the ISV's app identity, on behalf of one end user.
///
/// This replaces the Fabric IQ ExecuteQuery tool. IQ would run the query as the signed-in
/// Entra user; ISV end users are not Entra users, so instead every query carries a fixed RLS
/// role plus the user's key, and the model's row-level security decides which rows exist.
/// </summary>
public sealed class DaxQueryClient(PowerBiAppIdentity appIdentity, IOptions<FabricSettings> options)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<QueryResult> ExecuteAsync(AppUser user, string dax, CancellationToken cancellationToken)
    {
        var fabric = options.Value;
        var url = $"https://api.powerbi.com/v1.0/myorg/groups/{fabric.WorkspaceId}/datasets/{fabric.SemanticModelId}/executeDaxQueries";

        // The user's key travels in exactly one place, chosen by configuration:
        //   customData        -> read by CUSTOMDATA() in the RLS role
        //   effectiveUsername -> read by USERNAME() in the RLS role
        var body = new ExecuteDaxQueriesRequest(
            Query: dax,
            Roles: [fabric.RlsRole],
            CustomData: fabric.IdentityMode == IdentityMode.CustomData ? user.UserKey : null,
            EffectiveUsername: fabric.IdentityMode == IdentityMode.EffectiveUsername ? user.UserKey : null);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await appIdentity.GetTokenAsync(cancellationToken));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.apache.arrow.stream"));

        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ToolException($"executeDaxQueries returned HTTP {(int)response.StatusCode}. {Truncate(detail, 500)}");
        }
        return ArrowResults.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "...";

    private sealed record ExecuteDaxQueriesRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("roles")] string[] Roles,
        [property: JsonPropertyName("customData")] string? CustomData,
        [property: JsonPropertyName("effectiveUsername")] string? EffectiveUsername,
        [property: JsonPropertyName("queryTimeout")] int QueryTimeout = 30,
        [property: JsonPropertyName("resultSetRowCountLimit")] int ResultSetRowCountLimit = 1000);
}

/// <summary>An error that is returned to the model or MCP client as a tool error.</summary>
public sealed class ToolException(string message) : Exception(message);
