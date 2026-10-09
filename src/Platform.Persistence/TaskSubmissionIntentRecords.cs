using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class TaskSubmissionIntentRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public Guid OperationId { get; set; }
    public Guid DataSourceId { get; set; }
    public int InputVersion { get; set; }
    public int MaxAttempts { get; set; }
    public string Question { get; set; } = "";
    public string InputFingerprint { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

internal static class TaskSubmissionIntentMapping
{
    internal static void ConfigureTaskSubmissionIntents(this ModelBuilder model)
    {
        model.Entity<TaskSubmissionIntentRecord>(entity =>
        {
            entity.ToTable("TaskSubmissionIntents", table =>
            {
                table.HasCheckConstraint("CK_TaskSubmissionIntents_Input", "[InputVersion] = 1 AND [MaxAttempts] = 3");
                table.HasCheckConstraint("CK_TaskSubmissionIntents_Lifetime", "[ExpiresAtUtc] = DATEADD(hour,24,[CreatedAtUtc])");
                table.HasCheckConstraint("CK_TaskSubmissionIntents_Question", "DATALENGTH([Question]) BETWEEN 2 AND 8000");
            });
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.UserId, row.OperationId });
            entity.Property(row => row.Question).HasMaxLength(CustomerPilotTaskRequest.MaximumQuestionLength)
                .UseCollation("Latin1_General_100_BIN2");
            entity.Property(row => row.InputFingerprint).HasMaxLength(64).IsFixedLength().IsUnicode(false)
                .UseCollation("Latin1_General_100_BIN2");
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.UserId, row.CreatedAtUtc, row.OperationId })
                .IsDescending(false, false, false, true, true);
            entity.HasOne<CompanyMembershipRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.CompanyId, row.UserId })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
