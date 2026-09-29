using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using IqRls.Core;

namespace IqRls.Demo;

public sealed class CertificateDemoQuery(DemoStartup startup) : IDemoQuery, IDisposable
{
    private readonly HttpClient http = CloudHttp.Create();

    public async Task<QueryResult> ExecuteAsync(ServerContext context, DaxToolInput input, CancellationToken cancellationToken)
    {
        if (!startup.Enabled)
            throw new DemoException("live_not_enabled", "Live execution is disabled.");
        if (!OperatingSystem.IsWindows())
            throw new DemoException("query_auth_failed", "The demo requires the configured Windows certificate store.", 502);
        using var tokens = new CertificateTokenSource(startup.QueryConfiguration!);
        return await new QueryBroker(http, tokens, startup.QueryConfiguration!)
            .ExecuteAsync(context, input, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => http.Dispose();
}

public static class CloudHttp
{
    public static HttpClient Create() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15)
    }) { Timeout = Timeout.InfiniteTimeSpan };
}

public sealed class PinnedAzureCliTokens : IAccessTokenSource
{
    public const string Scope = "https://ai.azure.com/.default";
    private readonly TokenCredential credential;
    private readonly LlmConfiguration configuration;
    private readonly IDemoClock clock;

    public PinnedAzureCliTokens(LlmConfiguration configuration, IDemoClock clock)
        : this(configuration, clock, new AzureCliCredential(new AzureCliCredentialOptions
        {
            // Azure CLI rejects --tenant together with --subscription. Validate the returned tenant below.
            Subscription = configuration.SubscriptionId.ToString("D"),
            ProcessTimeout = TimeSpan.FromSeconds(20),
            Diagnostics = { IsLoggingEnabled = false, IsLoggingContentEnabled = false }
        })) { }

    public PinnedAzureCliTokens(LlmConfiguration configuration, IDemoClock clock, TokenCredential credential)
    {
        this.configuration = configuration;
        this.clock = clock;
        this.credential = credential;
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken).ConfigureAwait(false);
            ValidateIdentity(token.Token, configuration, clock.UtcNow);
            return token.Token;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            throw new DemoException("llm_auth_failed", "The configured Azure CLI identity could not be verified.", 502);
        }
    }

    public static void ValidateIdentity(string token, LlmConfiguration configuration, DateTimeOffset now)
    {
        try
        {
            if (token.Length > 32768 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && !"-_.".Contains(c)))
                throw new FormatException();
            var parts = token.Split('.');
            if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace)) throw new FormatException();
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            if (root.GetProperty("tid").GetString() != configuration.TenantId.ToString("D") ||
                root.GetProperty("exp").GetInt64() <= now.ToUnixTimeSeconds())
                throw new FormatException();
            var principals = new[] { "upn", "preferred_username", "unique_name" }
                .Where(name => root.TryGetProperty(name, out _))
                .Select(name => root.GetProperty(name).GetString()).ToArray();
            if (principals.Length == 0 || principals.Any(p =>
                !string.Equals(p, configuration.ExpectedPrincipal, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException();
            // These claims only guard against a CLI account mix-up. Azure verifies the token signature/audience.
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DemoException("llm_auth_failed", "The configured Azure CLI identity could not be verified.", 502);
        }
    }
}

public sealed class ResponsesExplainer(HttpClient http, IAccessTokenSource tokens, LlmConfiguration configuration) : IDemoExplainer
{
    public const int MaxResponseBytes = 256 * 1024;

    public async Task<LlmAnswer> ExplainAsync(string preparedInput, CancellationToken cancellationToken)
    {
        if (preparedInput.Length + ExplanationPrompt.Instructions.Length > ExplanationPrompt.MaxCharacters)
            throw new DemoException("result_too_large", "The authorized result is too large to explain safely.", 422);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var token = await tokens.GetTokenAsync(deadline.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(configuration.Endpoint, "openai/v1/responses"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = JsonContent.Create(new
            {
                model = configuration.Deployment,
                store = false,
                max_output_tokens = 1024,
                reasoning = new { effort = "low" },
                instructions = ExplanationPrompt.Instructions,
                input = preparedInput
            });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new DemoException("llm_http_failure", "The model service rejected the explanation request.", 502);
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new DemoException("llm_response_too_large", "The model response exceeded the local limit.", 502);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (buffer.Length + count > MaxResponseBytes)
                    throw new DemoException("llm_response_too_large", "The model response exceeded the local limit.", 502);
                buffer.Write(chunk, 0, count);
            }
            return ParseResponse(buffer.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DemoException("llm_timeout", "The model did not finish within its time limit.", 504);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new DemoException("llm_transport_failed", "The model service could not be reached.", 502);
        }
    }

    public static LlmAnswer ParseResponse(byte[] body)
    {
        try
        {
            if (body.Length > MaxResponseBytes)
                throw new DemoException("llm_response_too_large", "The model response exceeded the local limit.", 502);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.GetProperty("status").GetString() != "completed")
                throw new DemoException("llm_incomplete", "The model did not complete its explanation.", 502);
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                throw new DemoException("llm_invalid_response", "The model response was not usable.", 502);
            var text = new StringBuilder();
            foreach (var output in root.GetProperty("output").EnumerateArray())
            {
                var type = output.GetProperty("type").GetString();
                if (type == "reasoning") continue;
                if (type != "message" || output.GetProperty("role").GetString() != "assistant" ||
                    output.GetProperty("status").GetString() != "completed")
                    throw new DemoException("llm_invalid_response", "The model response was not usable.", 502);
                foreach (var content in output.GetProperty("content").EnumerateArray())
                {
                    if (content.GetProperty("type").GetString() == "refusal")
                        throw new DemoException("llm_refused", "The model declined to provide an explanation.", 502);
                    if (content.GetProperty("type").GetString() != "output_text")
                        throw new DemoException("llm_invalid_response", "The model response was not usable.", 502);
                    var part = content.GetProperty("text").GetString();
                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        if (text.Length > 0) text.AppendLine();
                        text.Append(part);
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(text.ToString()))
                throw new DemoException("llm_no_answer", "The model returned no usable explanation.", 502);
            var usage = root.GetProperty("usage");
            var inputTokens = usage.GetProperty("input_tokens").GetInt32();
            var outputTokens = usage.GetProperty("output_tokens").GetInt32();
            var model = root.GetProperty("model").GetString();
            if (inputTokens < 0 || outputTokens < 0 || string.IsNullOrWhiteSpace(model) || model.Length > 256)
                throw new DemoException("llm_invalid_response", "The model response omitted valid model or usage details.", 502);
            return new(text.ToString(), model, inputTokens, outputTokens);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new DemoException("llm_invalid_response", "The model response was not usable.", 502);
        }
    }
}

public sealed class LiveDemoExplainer(DemoStartup startup, IDemoClock clock) : IDemoExplainer, IDisposable
{
    private readonly HttpClient http = CloudHttp.Create();

    public Task<LlmAnswer> ExplainAsync(string preparedInput, CancellationToken cancellationToken)
    {
        if (!startup.Enabled) throw new DemoException("live_not_enabled", "Live execution is disabled.");
        var configuration = startup.Configuration!.Llm;
        return new ResponsesExplainer(http, new PinnedAzureCliTokens(configuration, clock), configuration)
            .ExplainAsync(preparedInput, cancellationToken);
    }

    public void Dispose() => http.Dispose();
}
