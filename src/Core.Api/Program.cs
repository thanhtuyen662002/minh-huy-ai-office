using MinhHuy.AIOffice.Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = ProjectInfo.ProductName,
    component = "Core.Api",
    status = "ok"
}));

app.MapHealthChecks("/health");

app.Run();

public partial class Program;
