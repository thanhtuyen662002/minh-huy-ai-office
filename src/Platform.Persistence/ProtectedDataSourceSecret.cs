using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MinhHuy.AIOffice.Platform.Configuration;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>Runtime-managed customer binding, separate from immutable operator grants.</summary>
public sealed class ProtectedDataSourceSecretRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public long Version { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string Reference => $"secretref://protected/{Id:D}";
}

public sealed class CustomerErpProbeAuditRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid ActorUserId { get; set; }
    public string Code { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed class ProtectedSecretResolver(PlatformDbContext database, ProtectedSecretEncryption encryption) : ISecretResolver
{
    public string Provider => "protected";
    public async ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        if (reference.Provider != Provider || !Guid.TryParseExact(reference.Resource, "D", out var id)
            || reference.Value != $"secretref://protected/{id:D}") throw DataSourceSecretBindingService.Unavailable();
        var row = await database.ProtectedDataSourceSecrets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null || row.Version < 1) throw DataSourceSecretBindingService.Unavailable();
        return encryption.Decrypt(row.TenantId, row.CompanyId, row.Id, new(row.Ciphertext, row.Nonce, row.Tag));
    }
}

public static class ProtectedSecretServices
{
    public static IServiceCollection AddProtectedDataSourceSecrets(this IServiceCollection services)
    {
        services.AddSingleton(new ProtectedSecretEncryption());
        services.AddScoped<ProtectedSecretResolver>();
        services.AddScoped(provider => new CompositeSecretResolver([
            new EnvironmentVariableSecretResolver(), provider.GetRequiredService<ProtectedSecretResolver>()]));
        return services;
    }
}
