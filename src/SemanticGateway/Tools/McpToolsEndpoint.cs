using System.Text.Json;
using ModelContextProtocol.Protocol;
using SemanticGateway.Identity;

namespace SemanticGateway.Tools;

/// <summary>
/// The gateway's MCP endpoint, built with the official MCP C# SDK. Whatever MCP client an ISV's
/// customer chooses (GitHub Copilot, VS Code, Claude, their own agent) calls the same three tools
/// as the portal's chat agent, under the same row-level security.
/// </summary>
public static class McpToolsEndpoint
{
    public static IServiceCollection AddSemanticModelMcpServer(this IServiceCollection services)
    {
        services.AddMcpServer(options => options.ServerInfo = new Implementation { Name = "isv-semantic-gateway", Version = "1.0.0" })
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((request, cancellationToken) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = SemanticModelTools.Definitions.Select(tool => new Tool
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchema = JsonDocument.Parse(tool.InputSchema).RootElement.Clone(),
                    Annotations = new ToolAnnotations { ReadOnlyHint = true, OpenWorldHint = false }
                }).ToList()
            }))
            .WithCallToolHandler(async (request, cancellationToken) =>
            {
                var services = request.Services!;
                AppUser user;
                try
                {
                    // The MCP request carries the HTTP user, already validated from the bearer token.
                    user = services.GetRequiredService<AppUserDirectory>().Resolve(request.User!);
                }
                catch (UnauthorizedAccessException error)
                {
                    return new CallToolResult { Content = [new TextContentBlock { Text = error.Message }], IsError = true };
                }

                var result = await services.GetRequiredService<SemanticModelTools>()
                    .InvokeAsync(user, request.Params!.Name, request.Params.Arguments, cancellationToken);
                return new CallToolResult { Content = [new TextContentBlock { Text = result.Text }], IsError = result.IsError };
            });
        return services;
    }
}
