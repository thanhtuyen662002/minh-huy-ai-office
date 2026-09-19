using MinhHuy.AIOffice.Agent.Worker;
using MinhHuy.AIOffice.Platform.Configuration;

var builder = Host.CreateApplicationBuilder(args);
_ = DeploymentEnvironment.Parse(builder.Environment.EnvironmentName);

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
