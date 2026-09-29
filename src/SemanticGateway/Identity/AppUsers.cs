using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SemanticGateway.Identity;

/// <summary>A user of the ISV application, as the semantic model knows them.</summary>
/// <param name="Subject">The <c>sub</c> claim issued by the ISV's identity provider.</param>
/// <param name="UserKey">The opaque key the model's RLS matches ('User Access'[Subject Key]).</param>
public sealed record AppUser(string Subject, string DisplayName, string UserKey);

/// <summary>
/// Maps an authenticated subject to the key the semantic model uses for row-level security.
/// In production this is your user or tenant directory; here it is a JSON file of synthetic users.
/// </summary>
public sealed class AppUserDirectory
{
    private readonly Dictionary<string, AppUser> users;

    public AppUserDirectory(IOptions<AuthSettings> settings, IWebHostEnvironment environment)
    {
        var path = Path.GetFullPath(Path.Combine(environment.ContentRootPath, settings.Value.UsersFile));
        var list = JsonSerializer.Deserialize<List<AppUser>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        users = list.ToDictionary(u => u.Subject, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<AppUser> All => users.Values;

    public bool Contains(string subject) => users.ContainsKey(subject);

    /// <summary>The user always comes from the validated token, never from request bodies or tool arguments.</summary>
    public AppUser Resolve(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst("sub")?.Value;
        return subject is not null && users.TryGetValue(subject, out var user)
            ? user
            : throw new UnauthorizedAccessException("The signed-in user has no access to this semantic model.");
    }
}
