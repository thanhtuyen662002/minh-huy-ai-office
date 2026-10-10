using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Agent.Worker;

public static class GroupIngressPipelineRegistration
{
    // This trusted process binding is separate from HTTP/broker inputs. Each
    // process owns one company/service queue; additional companies use separately
    // configured workers. SQL grants still select and authorize each source.
    public static bool AddGroupIngressReferenceProducer(this IServiceCollection services, IConfiguration configuration, bool hasPlatformDatabase)
    {
        var worker = ReadWorker(configuration, hasPlatformDatabase);
        if (worker is null) return false;
        AddCommon(services, worker);
        services.AddSingleton<IGroupIngressReferencePublisher, RabbitMqGroupIngressPublisher>();
        services.AddScoped<GroupIngressOutboxDispatcher>();
        services.AddHostedService<GroupIngressReferenceOutboxHostedService>();
        return true;
    }

    public static bool AddGroupIngressReferenceConsumer(this IServiceCollection services, IConfiguration configuration, bool hasPlatformDatabase)
    {
        var worker = ReadWorker(configuration, hasPlatformDatabase);
        if (worker is null) return false;
        AddCommon(services, worker);
        services.AddScoped<GroupIngressInboxStore>();
        services.AddHostedService<RabbitMqGroupIngressConsumer>();
        return true;
    }

    private static void AddCommon(IServiceCollection services, GroupExtractionWorkerBinding worker)
    {
        // Two hosted roles in the same process may share exactly one trusted
        // binding. Never let a later registration replace an existing authority.
        var existing = services.Where(descriptor => descriptor.ServiceType == typeof(GroupExtractionWorkerBinding)).ToArray();
        if (existing.Any(descriptor => descriptor.ImplementationInstance is not GroupExtractionWorkerBinding binding || binding != worker))
            throw Refused();
        services.TryAddSingleton(worker);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddScoped<GroupIngressPermissionVerifier>();
    }

    internal static GroupExtractionWorkerBinding? ReadWorker(IConfiguration configuration, bool hasPlatformDatabase)
    {
        if (configuration["AIOffice:GroupIntake:PipelineEnabled"] != "true") return null;
        if (!hasPlatformDatabase || configuration["AIOffice:GroupIntake:Enabled"] != "true") throw Refused();
        var section = configuration.GetSection("AIOffice:GroupIntake:Worker");
        static Guid Id(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value ? id : throw Refused();
        var epoch = section["CredentialEpoch"];
        if (!long.TryParse(epoch, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0 ||
            number.ToString(CultureInfo.InvariantCulture) != epoch) throw Refused();
        var worker = new GroupExtractionWorkerBinding(Id(section["TenantId"]), Id(section["CompanyId"]), Id(section["ServiceId"]), number);
        worker.Validate();
        return worker;
    }

    private static InvalidOperationException Refused() => new("Group reference pipeline configuration is not available.");
}
