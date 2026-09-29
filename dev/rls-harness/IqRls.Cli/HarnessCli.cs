using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Compression;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using IqRls.Core;

public static class HarnessCli
{
    public const string Help = """
        Local developer proof harness (.NET 8). No web server or browser-user authentication.

        Commands:
          --help
          generate-config-template
          selftest
          query --config <absolute-external-json-path> --subject <fixture-key> --dax-file <path> [--identity-mode customData|effectiveUsername] --allow-live
          verify-rls --config <absolute-external-json-path> [--identity-mode customData|effectiveUsername] --allow-live

        --subject selects a known synthetic server fixture; it does NOT authenticate a user.
        Live commands use application certificate auth: CurrentUser\My thumbprint or PEM files.
        Public IDs/thumbprint config must be outside this repository. No secret/private-key files.
        Exact cloud approval and pre-provisioned isolated synthetic model are prerequisites.
        --allow-live is an accidental-execution guard, not organizational approval.
        verify-rls: all six subjects, fixed probes, two seconds between requests (42 requests).
        JSON stdout is typed/lossless, synthetic-only. No token/raw error logging or file output.
        Selftest and unit tests do not prove model-engine RLS or Fabric IQ integration.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (args.Length == 0 || args is ["--help"] or ["help"])
            {
                await output.WriteLineAsync(Help);
                return 0;
            }
            if (args is ["generate-config-template"])
            {
                await output.WriteLineAsync(LiveConfiguration.Template);
                return 0;
            }
            if (args is ["selftest"])
            {
                SelfTest();
                await output.WriteLineAsync("""{"passed":true,"mode":"local-synthetic-arrow","liveExecuted":false,"modelRlsTested":false}""");
                return 0;
            }
            var options = ParseArguments(args);
            var configuration = (await LoadConfigurationAsync(options["--config"], cancellationToken))
                .WithIdentityMode(ParseIdentityMode(options.GetValueOrDefault("--identity-mode")));
            var subject = args[0] == "query" ? SyntheticSubjects.Resolve(options["--subject"]) : null;
            DaxToolInput? query = null;
            if (args[0] == "query")
            {
                var file = new FileInfo(options["--dax-file"]);
                if (!file.Exists || file.Length > HarnessContract.MaxDaxCharacters * 4)
                    throw new HarnessException(FailureCode.InvalidToolInput);
                query = new(await File.ReadAllTextAsync(file.FullName, cancellationToken));
                query.Validate();
            }
            using var tokens = new CertificateTokenSource(configuration);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(65) };
            var broker = new QueryBroker(client, tokens, configuration);
            if (args[0] == "query")
            {
                var result = await new DaxTool(broker, subject!).ExecuteAsync(query!, cancellationToken);
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    principalMode = "application-certificate",
                    liveExecuted = true,
                    subject = subject!.SubjectKey,
                    result
                }));
                return 0;
            }
            var report = await RlsVerifier.VerifyAsync(broker.ExecuteAsync, liveExecuted: true,
                cancellationToken, ct => Task.Delay(TimeSpan.FromSeconds(2), ct));
            await output.WriteLineAsync(JsonSerializer.Serialize(report));
            return report.Passed ? 0 : 2;
        }
        catch (HarnessException failure)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(new { error = failure.Code.ToString() }));
            return 2;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("""{"error":"Cancelled"}""");
            return 2;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await error.WriteLineAsync("""{"error":"LocalInputFailure"}""");
            return 2;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
        {
            // Last CLI boundary: never let runtime/SDK messages print certificate, request or auth data.
            await error.WriteLineAsync("""{"error":"UnexpectedFailure"}""");
            return 2;
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        if (args[0] is not ("query" or "verify-rls")) throw new HarnessException(FailureCode.InvalidArguments);
        var required = args[0] == "query"
            ? new[] { "--config", "--subject", "--dax-file" }
            : ["--config"];
        var optional = new[] { "--identity-mode" };
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var live = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--allow-live")
            {
                if (live) throw new HarnessException(FailureCode.InvalidArguments);
                live = true;
            }
            else if ((required.Contains(args[i], StringComparer.Ordinal) || optional.Contains(args[i], StringComparer.Ordinal)) && i + 1 < args.Length)
            {
                var key = args[i];
                if (!options.TryAdd(key, args[++i])) throw new HarnessException(FailureCode.InvalidArguments);
            }
            else throw new HarnessException(FailureCode.InvalidArguments);
        }
        if (!live || required.Any(r => !options.ContainsKey(r)) || options.Keys.Any(k => !required.Contains(k, StringComparer.Ordinal) && !optional.Contains(k, StringComparer.Ordinal)))
            throw new HarnessException(FailureCode.InvalidArguments);
        return options;
    }

    private static QueryIdentityMode ParseIdentityMode(string? value) => value switch
    {
        null or "customData" => QueryIdentityMode.CustomData,
        "effectiveUsername" => QueryIdentityMode.EffectiveUsername,
        _ => throw new HarnessException(FailureCode.InvalidArguments)
    };

    private static async Task<LiveConfiguration> LoadConfigurationAsync(string path, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(path)) throw new HarnessException(FailureCode.InvalidConfiguration);
        var fullPath = Path.GetFullPath(path);
        foreach (var startingDirectory in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(startingDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "fabric-iq-app-identity-rls.sln")) &&
                    fullPath.StartsWith(directory.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new HarnessException(FailureCode.InvalidConfiguration);
            }
        }
        var file = new FileInfo(fullPath);
        if (!file.Exists || file.Length > 16384 || file.LinkTarget is not null)
            throw new HarnessException(FailureCode.InvalidConfiguration);
        // Reject parent junctions/symlinks rather than silently following a path back into the repository.
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null || File.Exists(Path.Combine(directory.FullName, "fabric-iq-app-identity-rls.sln")))
                throw new HarnessException(FailureCode.InvalidConfiguration);
        return LiveConfiguration.Parse(await File.ReadAllTextAsync(fullPath, cancellationToken));
    }

    private static void SelfTest()
    {
        var schema = new Schema([new Field("Value", Int64Type.Default, true)], null);
        using var array = new Int64Array.Builder().Append(long.MaxValue).AppendNull().Build();
        using var batch = new RecordBatch(schema, [array], 2);
        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, schema, leaveOpen: true, new IpcOptions
        {
            CompressionCodec = CompressionCodecType.Lz4Frame,
            CompressionCodecFactory = new CompressionCodecFactory()
        }))
        {
            writer.WriteRecordBatch(batch);
            writer.WriteEnd();
        }
        var payload = stream.ToArray();
        var parsed = ArrowResponseParser.Parse(payload);
        if (parsed.Rows.Length != 2 || parsed.Rows[0][0] != "9223372036854775807" || parsed.Rows[1][0] is not null)
            throw new HarnessException(FailureCode.VerificationFailed);
        foreach (var invalid in new[] { payload[..^1], payload[..^8], payload.Concat(payload).ToArray() })
        {
            try
            {
                ArrowResponseParser.Parse(invalid);
            }
            catch (HarnessException failure) when (failure.Code is FailureCode.InvalidArrow or FailureCode.UnexpectedResultSets)
            {
                continue;
            }
            throw new HarnessException(FailureCode.VerificationFailed);
        }
        foreach (var subject in SyntheticSubjects.All) _ = SyntheticSubjects.Resolve(subject.SubjectKey);
    }
}
