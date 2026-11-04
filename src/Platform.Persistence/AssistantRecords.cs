using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

// A picture a member attached to a Trợ lý question. Only its uploader can read it or have the AI look at it.
public sealed class AssistantImageRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid UploadedByUserId { get; set; }
    public required string ContentType { get; set; }
    public byte[] Content { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }
}

// A Trợ lý question its asker deleted from their history. The task itself stays for audit.
public sealed class HiddenTaskRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid TaskId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset HiddenAtUtc { get; set; }
}

internal static class AssistantModelConfiguration
{
    internal static void ConfigureAssistant(this ModelBuilder model)
    {
        model.Entity<AssistantImageRecord>(entity =>
        {
            entity.ToTable("AssistantImages");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.ContentType).HasMaxLength(32);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.UploadedByUserId, x.CreatedAtUtc });
        });
        model.Entity<HiddenTaskRecord>(entity =>
        {
            entity.ToTable("HiddenTasks");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.TaskId });
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.UserId });
        });
    }
}
