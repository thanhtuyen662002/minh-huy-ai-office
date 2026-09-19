using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);
var deploymentEnvironment = DeploymentEnvironment.Parse(builder.Environment.EnvironmentName);

string? platformConnectionString = null;
var platformConnectionSecretReference =
    builder.Configuration["AIOffice:PlatformDatabase:ConnectionSecretRef"];

if (!string.IsNullOrWhiteSpace(platformConnectionSecretReference))
{
    var secretResolver = new CompositeSecretResolver(
        new ISecretResolver[] { new EnvironmentVariableSecretResolver() });

    platformConnectionString = await secretResolver.ResolveAsync(
        SecretReference.Parse(platformConnectionSecretReference));
}

builder.Services.AddHealthChecks();
builder.Services.AddPlatformPersistence(platformConnectionString);

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = ProjectInfo.ProductName,
    component = "Core.Api",
    environment = deploymentEnvironment.ToString(),
    status = "ok"
}));

app.MapHealthChecks("/health");

app.Run();

public partial class Program;
