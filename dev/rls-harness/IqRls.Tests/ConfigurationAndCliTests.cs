using System.Text.Json.Nodes;
using IqRls.Core;

namespace IqRls.Tests;

public sealed class ConfigurationAndCliTests
{
    [Fact]
    public void ConfigurationPinsHostRoleAndPrincipalMode()
    {
        var configuration = TestData.Configuration;
        Assert.Equal("certificate", configuration.AuthMode);
        Assert.Equal("api.powerbi.com", configuration.Endpoint.Host);
        Assert.Equal("https", configuration.Endpoint.Scheme);
        Assert.EndsWith("/executeDaxQueries", configuration.Endpoint.AbsolutePath);
        Assert.Equal(QueryIdentityMode.CustomData, configuration.IdentityMode);
        Assert.Equal("ExternalAppScope", configuration.Role);
        Assert.All(typeof(LiveConfiguration).GetProperties(), p => Assert.Null(p.SetMethod));
    }

    [Theory]
    [InlineData("privateKey", "SECRET_SENTINEL")]
    [InlineData("clientSecret", "SECRET_SENTINEL")]
    [InlineData("host", "https://example.invalid")]
    [InlineData("effectiveUsername", "user@example.invalid")]
    public void AdditionalConfigFieldsAreRejectedWithoutEchoingThem(string field, string value)
    {
        var json = JsonNode.Parse(TestData.ConfigurationJson)!;
        json[field] = value;
        var error = Assert.Throws<HarnessException>(() => LiveConfiguration.Parse(json.ToJsonString()));
        Assert.Equal(FailureCode.InvalidConfiguration, error.Code);
        Assert.DoesNotContain("SECRET", error.ToString());
    }


    [Fact]
    public void EffectiveUsernameConfigurationPinsUsernameRole()
    {
        var json = JsonNode.Parse(TestData.ConfigurationJson)!;
        json["identityMode"] = "effectiveUsername";
        json["role"] = HarnessContract.UsernameRole;
        var configuration = LiveConfiguration.Parse(json.ToJsonString());
        Assert.Equal(QueryIdentityMode.EffectiveUsername, configuration.IdentityMode);
        Assert.Equal(HarnessContract.UsernameRole, configuration.Role);
    }

    [Fact]
    public void PemCertificateSourceRequiresAbsoluteCertificateAndKeyWithoutThumbprint()
    {
        var json = JsonNode.Parse(TestData.ConfigurationJson)!;
        json.AsObject().Remove("certificateThumbprint");
        json["certificatePemPath"] = @"C:\certs\app.pem";
        json["privateKeyPemPath"] = @"C:\certs\app.key";
        var configuration = LiveConfiguration.Parse(json.ToJsonString());
        Assert.Equal(@"C:\certs\app.pem", configuration.CertificatePemPath);
        Assert.Equal(@"C:\certs\app.key", configuration.PrivateKeyPemPath);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void CertificateSourcesAreMutuallyExclusiveAndComplete(bool keepThumbprint, bool certPem, bool keyPem)
    {
        var json = JsonNode.Parse(TestData.ConfigurationJson)!;
        if (!keepThumbprint) json.AsObject().Remove("certificateThumbprint");
        if (certPem) json["certificatePemPath"] = @"C:\certs\app.pem";
        if (keyPem) json["privateKeyPemPath"] = @"C:\certs\app.key";
        Assert.Equal(FailureCode.InvalidConfiguration,
            Assert.Throws<HarnessException>(() => LiveConfiguration.Parse(json.ToJsonString())).Code);
    }

    [Theory]
    [InlineData("authMode", "default")]
    [InlineData("authMode", "azure-cli")]
    [InlineData("role", "Admin")]
    [InlineData("modelAlias", "other")]
    [InlineData("entitlementVersion", "stale")]
    [InlineData("tenantId", "common")]
    [InlineData("clientId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("certificateThumbprint", "private.pfx")]
    public void InvalidConfigurationValuesFailClosed(string field, string value)
    {
        var json = JsonNode.Parse(TestData.ConfigurationJson)!;
        json[field] = value;
        Assert.Equal(FailureCode.InvalidConfiguration,
            Assert.Throws<HarnessException>(() => LiveConfiguration.Parse(json.ToJsonString())).Code);
    }

    [Fact]
    public void DuplicateConfigurationFieldsAreRejected()
    {
        var json = TestData.ConfigurationJson.Replace("\"authMode\":\"certificate\",",
            "\"authMode\":\"certificate\",\"authMode\":\"certificate\",", StringComparison.Ordinal);
        Assert.Equal(FailureCode.InvalidConfiguration, Assert.Throws<HarnessException>(() => LiveConfiguration.Parse(json)).Code);
    }

    [Fact]
    public async Task HelpAndSelftestCannotRequireNetworkOrCredentials()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await HarnessCli.RunAsync(["--help"], output, error));
        Assert.Contains("does NOT authenticate", output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await HarnessCli.RunAsync(["selftest"], output, error));
        Assert.Contains("\"liveExecuted\":false", output.ToString());
        Assert.Contains("\"modelRlsTested\":false", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task TemplateContainsOnlyPlaceholdersAndCannotBeUsedLive()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await HarnessCli.RunAsync(["generate-config-template"], output, error));
        Assert.Contains("<tenant-guid>", output.ToString());
        Assert.Throws<HarnessException>(() => LiveConfiguration.Parse(output.ToString()));
    }

    [Theory]
    [InlineData("query")]
    [InlineData("verify-rls")]
    public async Task LiveCommandsNeedExplicitExecutionFlagBeforeAnyFileOrCertificateAccess(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await HarnessCli.RunAsync([command, "--config", @"C:\nonexistent\config.json"], output, error));
        Assert.Contains("InvalidArguments", error.ToString());
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task ConfigInsideRepositoryIsRejectedBeforeLoading()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "fabric-iq-app-identity-rls.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "config-that-does-not-exist.json");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await HarnessCli.RunAsync(["verify-rls", "--config", path, "--allow-live"], output, error));
        Assert.Contains("InvalidConfiguration", error.ToString());
        Assert.DoesNotContain(path, error.ToString());
    }
}
