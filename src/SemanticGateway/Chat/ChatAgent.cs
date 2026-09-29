using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using SemanticGateway.Fabric;
using SemanticGateway.Identity;
using SemanticGateway.Tools;

namespace SemanticGateway.Chat;

public sealed record ChatTurn(string Role, string Content);
public sealed record ChatRequest(string Message, ChatTurn[]? History);
public sealed record ChatEvent(string Step, string Status, string Name, long ElapsedMs, Dictionary<string, string> Details);
public sealed record ToolCallSummary(string Name, string Arguments, string? Dax, int? RowCount, bool IsError);
public sealed record ChatUsage(string Model, int ModelCalls, int InputTokens, int OutputTokens);
public sealed record ChatResult(string Answer, IReadOnlyList<ToolCallSummary> ToolCalls, QueryResult? Table, ChatUsage Usage);

/// <summary>
/// The portal's chat agent: an Azure OpenAI Responses API tool loop over the same three tools
/// the MCP endpoint exposes. The model decides which tools to call; the gateway runs every call
/// as the signed-in user, so the model never sees rows that user cannot see.
/// </summary>
public sealed class ChatAgent(SemanticModelTools tools, IOptions<AzureOpenAISettings> aiOptions, IOptions<FabricSettings> fabricOptions)
{
    private const int MaxModelCalls = 6;
    private const int MaxToolCalls = 10;

    private const string Instructions =
        "You are the analytics assistant inside the ISV's application. Answer the user's question about their data " +
        "by calling the tools: get the schema first, resolve text values with search_values when needed, then run DAX " +
        "with execute_dax. Row-level security is applied for the signed-in user automatically. " +
        "If search_values finds nothing, the value does not exist or is not visible to this user: say so and do not retry. " +
        "Answer in one to three plain sentences that name the values involved (for example customer and product) " +
        "and use only numbers returned by the tools. Do not format the answer as a table; the application shows the rows separately.";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    // In Azure: the app's managed identity. Locally: your Azure CLI sign-in (az login).
    private readonly TokenCredential credential = new ChainedTokenCredential(
        new ManagedIdentityCredential(),
        new AzureCliCredential(new AzureCliCredentialOptions { TenantId = fabricOptions.Value.TenantId }));

    public async Task<ChatResult> RunAsync(AppUser user, ChatRequest chat, Func<ChatEvent, Task> onEvent, CancellationToken cancellationToken)
    {
        var fabric = fabricOptions.Value;
        var clock = Stopwatch.StartNew();
        Task Emit(string step, string status, string name, Dictionary<string, string>? details = null) =>
            onEvent(new ChatEvent(step, status, name, clock.ElapsedMilliseconds, details ?? []));

        await Emit("authenticate", "completed", user.Subject, new()
        {
            ["userKey"] = user.UserKey,
            ["role"] = fabric.RlsRole,
            ["identityMode"] = fabric.IdentityMode.ToString()
        });

        var input = new JsonArray();
        foreach (var turn in chat.History ?? []) input.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
        input.Add(new JsonObject { ["role"] = "user", ["content"] = chat.Message });

        var toolCalls = new List<ToolCallSummary>();
        QueryResult? lastTable = null;
        int inputTokens = 0, outputTokens = 0;
        var deployment = aiOptions.Value.Deployment;

        for (var modelCalls = 1; modelCalls <= MaxModelCalls; modelCalls++)
        {
            await Emit("model", "started", deployment);
            var response = await CallResponsesApiAsync(input, cancellationToken);
            inputTokens += (int?)response["usage"]?["input_tokens"] ?? 0;
            outputTokens += (int?)response["usage"]?["output_tokens"] ?? 0;
            await Emit("model", "completed", deployment, new()
            {
                ["inputTokens"] = inputTokens.ToString(),
                ["outputTokens"] = outputTokens.ToString()
            });

            var output = response["output"]!.AsArray();
            foreach (var item in output) input.Add(item!.DeepClone());

            var calls = output.Where(item => (string?)item?["type"] == "function_call").ToList();
            if (calls.Count == 0)
            {
                var answer = string.Join("\n", output
                    .Where(item => (string?)item?["type"] == "message")
                    .SelectMany(item => item!["content"]!.AsArray())
                    .Where(part => (string?)part?["type"] == "output_text")
                    .Select(part => (string?)part!["text"]));
                await Emit("answer", "completed", deployment);
                return new ChatResult(answer, toolCalls, lastTable, new ChatUsage(deployment, modelCalls, inputTokens, outputTokens));
            }

            foreach (var call in calls)
            {
                if (toolCalls.Count == MaxToolCalls) throw new InvalidOperationException("The model requested too many tool calls.");
                var name = (string)call!["name"]!;
                var arguments = (string?)call["arguments"] ?? "{}";
                await Emit("tool", "started", name, new() { ["arguments"] = arguments });

                var result = await tools.InvokeAsync(user, name, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments), cancellationToken);
                lastTable = result.Table ?? lastTable;
                toolCalls.Add(new ToolCallSummary(name, arguments, result.Dax, result.Table?.Rows.Count, result.IsError));
                await Emit("tool", result.IsError ? "failed" : "completed", name, new()
                {
                    ["dax"] = result.Dax ?? "",
                    ["rowCount"] = result.Table?.Rows.Count.ToString() ?? "",
                    ["error"] = result.IsError ? result.Text : ""
                });

                input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = (string?)call["call_id"], ["output"] = result.Text });
            }
        }
        throw new InvalidOperationException("The model did not finish within the tool-call limit.");
    }

    private async Task<JsonNode> CallResponsesApiAsync(JsonArray input, CancellationToken cancellationToken)
    {
        var ai = aiOptions.Value;
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), cancellationToken);
        var request = new JsonObject
        {
            ["model"] = ai.Deployment,
            ["instructions"] = Instructions,
            ["input"] = input.DeepClone(),
            ["tools"] = new JsonArray(SemanticModelTools.Definitions.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.InputSchema)
            }).ToArray()),
            // Nothing is stored service-side; reasoning state travels encrypted in the conversation.
            ["store"] = false,
            ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["reasoning"] = new JsonObject { ["effort"] = "low" }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(ai.Endpoint), "openai/v1/responses"))
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await Http.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Azure OpenAI returned HTTP {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }
}
