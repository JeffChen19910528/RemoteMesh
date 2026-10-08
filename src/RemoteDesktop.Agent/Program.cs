using RemoteDesktop.Agent;

var builder = Host.CreateApplicationBuilder(args);

// Run as a proper service when installed as one (no effect when started from a console).
builder.Services.AddWindowsService(o => o.ServiceName = "RemoteMeshAgent");
builder.Services.AddSystemd();

builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();
