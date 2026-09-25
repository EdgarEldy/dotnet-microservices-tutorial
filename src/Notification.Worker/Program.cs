var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

var host = builder.Build();

host.Run();
