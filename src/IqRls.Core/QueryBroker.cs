using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace IqRls.Core;

public sealed class QueryBroker(HttpClient httpClient, IAccessTokenSource tokenSource, LiveConfiguration configuration)
{
    private sealed record WireQuery(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("roles")] string[] Roles,
        [property: JsonPropertyName("customData")] string CustomData,
        [property: JsonPropertyName("queryTimeout")] int QueryTimeout,
        [property: JsonPropertyName("resultSetRowCountLimit")] int ResultSetRowCountLimit);

    public async Task<QueryResult> ExecuteAsync(ServerContext context, DaxToolInput input,
        CancellationToken cancellationToken = default)
    {
        if (context is null || context.ModelAlias != configuration.ModelAlias ||
            context.EntitlementVersion != configuration.EntitlementVersion)
            throw new HarnessException(FailureCode.InvalidContext);
        _ = SyntheticSubjects.Resolve(context.SubjectKey);
        if (input is null) throw new HarnessException(FailureCode.InvalidToolInput);
        input.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            string token;
            try
            {
                token = await tokenSource.GetTokenAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (HarnessException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception failure) when (failure is not OutOfMemoryException and not AccessViolationException)
            {
                throw new HarnessException(FailureCode.AuthenticationFailed);
            }
            if (string.IsNullOrWhiteSpace(token) || token.Length > 32768 ||
                token.Any(c => !char.IsAsciiLetterOrDigit(c) && !"-.~+/=_".Contains(c)))
                throw new HarnessException(FailureCode.AuthenticationFailed);
            using var request = new HttpRequestMessage(HttpMethod.Post, configuration.Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.apache.arrow.stream"));
            request.Content = JsonContent.Create(new WireQuery(input.Query, [configuration.Role],
                context.SubjectKey, 30, HarnessContract.MaxRows));
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HarnessException(FailureCode.HttpFailure);
            if (response.Content.Headers.ContentLength > HarnessContract.MaxResponseBytes)
                throw new HarnessException(FailureCode.LimitExceeded);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            while (true)
            {
                var count = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (buffer.Length + count > HarnessContract.MaxResponseBytes)
                    throw new HarnessException(FailureCode.LimitExceeded);
                buffer.Write(chunk, 0, count);
            }
            return ArrowResponseParser.Parse(buffer.ToArray(), deadline.Token);
        }
        catch (OperationCanceledException)
        {
            throw new HarnessException(cancellationToken.IsCancellationRequested
                ? FailureCode.Cancelled : FailureCode.TransportFailed);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new HarnessException(FailureCode.TransportFailed);
        }
    }
}
