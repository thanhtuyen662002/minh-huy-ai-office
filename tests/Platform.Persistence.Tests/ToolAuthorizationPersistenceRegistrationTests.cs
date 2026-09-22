using Microsoft.Extensions.DependencyInjection;
using MinhHuy.AIOffice.Platform.Persistence;
using Platform.Persistence;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class ToolAuthorizationPersistenceRegistrationTests
{
    [Fact]
    public void Configured_persistence_registers_durable_authorization_audit_boundary()
    {
        var services = new ServiceCollection();

        services.AddPlatformPersistence("Server=(localdb)\\mssqllocaldb;Database=MinhHuyPolicyRegistration;Trusted_Connection=True;");

        Assert.Contains(services, x => x.ServiceType == typeof(ToolAuthorizationPolicy));
        Assert.Contains(services, x => x.ServiceType == typeof(IToolExecutionAuditSink) && x.ImplementationType == typeof(SqlToolExecutionAuditSink));
        Assert.Contains(services, x => x.ServiceType == typeof(ToolExecutionAuditService));
        Assert.Contains(services, x => x.ServiceType == typeof(AuthorizedToolExecutionGate));
    }

    [Fact]
    public void Missing_database_configuration_does_not_create_non_durable_audit_fallback()
    {
        var services = new ServiceCollection();

        services.AddPlatformPersistence(null);

        Assert.DoesNotContain(services, x => x.ServiceType == typeof(IToolExecutionAuditSink));
        Assert.DoesNotContain(services, x => x.ServiceType == typeof(ToolExecutionAuditService));
        Assert.DoesNotContain(services, x => x.ServiceType == typeof(AuthorizedToolExecutionGate));
    }
}
