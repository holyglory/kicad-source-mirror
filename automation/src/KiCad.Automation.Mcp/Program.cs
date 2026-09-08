using KiCad.Automation.Mcp;
using KiCad.Automation.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
string stateDirectory = builder.Configuration["state-directory"]
    ?? Environment.GetEnvironmentVariable("KICAD_AUTOMATION_STATE_DIRECTORY")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kicad-automation");
builder.Services.AddSingleton<INativeTransport, NngTransport>();
builder.Services.AddSingleton<FileIntakeRegistry>();
builder.Services.AddSingleton(provider => new InstanceRegistry(provider.GetRequiredService<INativeTransport>(), stateDirectory));
builder.Services.AddMcpServer(options =>
{
    options.ServerInstructions = "Operate only explicitly identified KiCad instances. Listed registrations are not proof a process is still live: inspect before use. Do not edit native design files behind a live editor. Mutations require explicit targets and the documented revision/retry identities. After a lost mutation reply, inspect its receipt or retry identical arguments with the same operation ID. Do not infer routing, complete revision tracking or XML reconstruction support from a successful connection.";
})
    .WithStdioServerTransport()
    .WithTools<InstanceTools>()
    .WithTools<EventTools>()
    .WithTools<SchematicViewTools>()
    .WithTools<SchematicMutationTools>()
    .WithTools<SchematicXmlTools>()
    .WithTools<DocumentTools>()
    .WithTools<KnowledgeTools>()
    .WithTools<PlacementTools>()
    .WithTools<RecoveryTools>()
    .WithTools<FileIntakeTools>();
await builder.Build().RunAsync();
