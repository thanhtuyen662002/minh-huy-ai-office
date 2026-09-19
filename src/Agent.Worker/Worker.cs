using MinhHuy.AIOffice.Platform.Observability;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Agent.Worker;

public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var activity = AiOfficeTelemetry.ActivitySource.StartActivity("agent.worker.start"))
        {
            logger.LogInformation("{Product} agent worker started", ProjectInfo.ProductName);
            AiOfficeTelemetry.WorkerLifecycleEvents.Add(
                1,
                new KeyValuePair<string, object?>("event", "started"));
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
