using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class WorkerExecutionModelConfiguration
{
    internal static void ConfigureWorkerExecution(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskStepExecutionRecord>(entity =>
        {
            entity.ToTable("TaskStepExecutions", PlatformDbContext.DefaultSchema);
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId });
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200);
            entity.Property(x => x.Attempt).HasDefaultValue(0);
            entity.Property(x => x.LeaseOwnerId).HasMaxLength(200);
            entity.Property(x => x.LeaseFenceToken).HasDefaultValue(0L);
            entity.Property(x => x.LastFailureClass).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.UpdatedAtUtc).HasDefaultValueSql("SYSDATETIMEOFFSET()");
            entity.Property(x => x.RowVersion).IsRowVersion();
            entity.HasOne<TaskStepRecord>().WithOne().HasForeignKey<TaskStepExecutionRecord>(x => new { x.TenantId, x.CompanyId, x.TaskId, Id = x.StepId }).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.LeaseExpiresAtUtc });
        });

        modelBuilder.Entity<TaskDispatchRecord>(entity =>
        {
            entity.ToTable("TaskDispatches", PlatformDbContext.DefaultSchema);
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId, x.MessageId });
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(40).HasDefaultValue(WorkDispatchState.Pending);
            entity.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSDATETIMEOFFSET()");
            entity.HasOne<TaskStepRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.TaskId, Id = x.StepId }).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId, x.Attempt }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.State, x.AvailableAtUtc });
        });
    }
}
