using ManagedCode.AgentLightning.AgentRuntime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
});

builder.Services.AddSingleton<IChatClient, LocalChatClient>();

builder.Services.AddLightningAgent(options =>
{
    options.AgentName = "cli";
    options.SystemPrompt = "You are the C# port of Agent Lightning. Provide concise, actionable answers.";
});

await using var host = builder.Build();
await using var scope = host.Services.CreateAsyncScope();

var agent = scope.ServiceProvider.GetRequiredService<LightningAgent>();

Console.WriteLine("ManagedCode Agent Lightning CLI — C# port of the Microsoft Agent Lightning reference. Type 'exit' to quit.\n");

Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    Environment.ExitCode = 0;
};

while (true)
{
    Console.Write("> ");
    var input = Console.ReadLine();

    if (input is null)
    {
        continue;
    }

    if (string.Equals(input, "exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    if (string.IsNullOrWhiteSpace(input))
    {
        Console.WriteLine("Please enter a message.");
        continue;
    }

    try
    {
        var result = await agent.ExecuteAsync(input);
        Console.WriteLine(result.Response.Text);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Execution failed: {ex.Message}");
    }
}
