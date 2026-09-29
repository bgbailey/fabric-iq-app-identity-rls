using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SemanticGateway.Identity;

/// <summary>
/// DEMO ONLY. A tiny token issuer so the sample runs without an external identity provider.
/// It signs the synthetic users in with a shared password and issues short-lived JWTs, which the
/// gateway validates exactly as it would validate tokens from your real identity provider.
/// In production set Auth:Mode to "Oidc" and point Auth:Authority at your provider instead.
/// </summary>
public sealed class DevIdentityProvider(IOptions<AuthSettings> options, AppUserDirectory users)
{
    // A new key per process: issued tokens stop working when the gateway restarts.
    public RsaSecurityKey SigningKey { get; } = new(RSA.Create(2048)) { KeyId = Guid.NewGuid().ToString("N") };

    public string IssueToken(string username, string password)
    {
        var settings = options.Value;
        if (password != settings.DevPassword || !users.Contains(username))
            throw new UnauthorizedAccessException("Unknown user or wrong password.");

        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims: [new Claim("sub", username), new Claim("name", username)],
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(settings.DevTokenMinutes),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public object OpenIdConfiguration() => new
    {
        issuer = options.Value.Issuer,
        token_endpoint = options.Value.Issuer + "/token",
        jwks_uri = options.Value.Issuer + "/jwks",
        id_token_signing_alg_values_supported = new[] { "RS256" }
    };

    public object Jwks()
    {
        var key = SigningKey.Rsa.ExportParameters(includePrivateParameters: false);
        return new
        {
            keys = new[]
            {
                new { kty = "RSA", use = "sig", alg = "RS256", kid = SigningKey.KeyId, n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent) }
            }
        };
    }
}

public static class DevIdentityProviderEndpoints
{
    public sealed record SignIn(string Username, string Password);

    /// <summary>DEMO ONLY: /dev-idp/token signs a synthetic user in; jwks and discovery describe the issuer.</summary>
    public static void MapDevIdentityProvider(this WebApplication app)
    {
        app.MapPost("/dev-idp/token", async (HttpRequest request, DevIdentityProvider issuer, IOptions<AuthSettings> settings) =>
        {
            var signIn = request.HasFormContentType
                ? new SignIn((await request.ReadFormAsync())["username"].ToString(), (await request.ReadFormAsync())["password"].ToString())
                : await request.ReadFromJsonAsync<SignIn>();
            try
            {
                var token = issuer.IssueToken(signIn?.Username ?? "", signIn?.Password ?? "");
                return Results.Json(new { access_token = token, token_type = "Bearer", expires_in = settings.Value.DevTokenMinutes * 60 });
            }
            catch (UnauthorizedAccessException error)
            {
                return Results.Json(new { error = "invalid_grant", error_description = error.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        app.MapGet("/dev-idp/.well-known/openid-configuration", (DevIdentityProvider issuer) => issuer.OpenIdConfiguration());
        app.MapGet("/dev-idp/jwks", (DevIdentityProvider issuer) => issuer.Jwks());
    }
}
