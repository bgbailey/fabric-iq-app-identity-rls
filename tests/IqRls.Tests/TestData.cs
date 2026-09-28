using System.Net;
using Apache.Arrow;
using Apache.Arrow.Compression;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using IqRls.Core;

namespace IqRls.Tests;

internal static class TestData
{
    internal const string ConfigurationJson = """
        {
          "authMode":"certificate",
          "tenantId":"11111111-1111-4111-8111-111111111111",
          "clientId":"22222222-2222-4222-8222-222222222222",
          "workspaceId":"33333333-3333-4333-8333-333333333333",
          "datasetId":"44444444-4444-4444-8444-444444444444",
          "certificateThumbprint":"0123456789012345678901234567890123456789",
          "modelAlias":"synthetic-rls-v1",
          "entitlementVersion":"synthetic-v1",
          "role":"ExternalAppScope"
        }
        """;

    internal static LiveConfiguration Configuration => LiveConfiguration.Parse(ConfigurationJson);

    internal static byte[] Arrow(Schema schema, IArrowArray[][] batches, bool compressed = true, bool legacy = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, schema, leaveOpen: true, new IpcOptions
        {
            WriteLegacyIpcFormat = legacy,
            CompressionCodec = compressed ? CompressionCodecType.Lz4Frame : null,
            CompressionCodecFactory = new CompressionCodecFactory()
        }))
        {
            writer.WriteStart();
            foreach (var arrays in batches)
            {
                using var batch = new RecordBatch(schema, arrays, arrays.Length == 0 ? 0 : arrays[0].Length);
                writer.WriteRecordBatch(batch);
            }
            writer.WriteEnd();
        }
        return stream.ToArray();
    }

    internal static byte[] Scalar(long value = 250, bool compressed = true, bool legacy = false) =>
        Arrow(new Schema([new Field("Total", Int64Type.Default, false)], null),
            [[new Int64Array.Builder().Append(value).Build()]], compressed, legacy);

    internal static byte[] Error() =>
        Arrow(new Schema([new Field("ErrorMessage", StringType.Default, true)],
            new Dictionary<string, string>
            {
                ["IsError"] = "true",
                ["FaultCode"] = "0x0000",
                ["FaultString"] = "SENSITIVE_FAULT_SENTINEL"
            }), [[new StringArray.Builder().Append("SENSITIVE_FAULT_SENTINEL").Build()]]);

    internal sealed class Tokens(string token = "synthetic-token-not-a-credential") : IAccessTokenSource
    {
        internal int Calls;
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(token);
        }
    }

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return action(request, cancellationToken);
        }
    }

    internal static HttpResponseMessage Response(byte[] payload) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
}
