using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string DefaultSchema = "aioffice";

    public DbSet<PlatformMetadataRecord> PlatformMetadata => Set<PlatformMetadataRecord>();

    public DbSet<PlatformUserRecord> Users => Set<PlatformUserRecord>();

    public DbSet<CompanyRecord> Companies => Set<CompanyRecord>();

    public DbSet<CompanyMembershipRecord> CompanyMemberships => Set<CompanyMembershipRecord>();

    public DbSet<RoleAssignmentRecord> RoleAssignments => Set<RoleAssignmentRecord>();

    public DbSet<TaskRecord> Tasks => Set<TaskRecord>();

    public DbSet<TaskStepRecord> TaskSteps => Set<TaskStepRecord>();

    public DbSet<TaskDependencyRecord> TaskDependencies => Set<TaskDependencyRecord>();

    public DbSet<TaskEventRecord> TaskEvents => Set<TaskEventRecord>();

    public DbSet<TaskCheckpointRecord> TaskCheckpoints => Set<TaskCheckpointRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DefaultSchema);

        modelBuilder.Entity<PlatformMetadataRecord>(entity =>
        {
            entity.ToTable("PlatformMetadata");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(200);
            entity.Property(x => x.Value).HasMaxLength(4000);
            entity.Property(x => x.UpdatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");
        });

        modelBuilder.Entity<PlatformUserRecord>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.IdentityProvider).HasMaxLength(100);
            entity.Property(x => x.Subject).HasMaxLength(200);
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");
            entity.HasIndex(x => new { x.TenantId, x.IdentityProvider, x.Subject })
                .IsUnique();
        });

        modelBuilder.Entity<CompanyRecord>(entity =>
        {
            entity.ToTable("Companies");
            entity.HasKey(x => new { x.TenantId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(100);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");
            entity.HasIndex(x => new { x.TenantId, x.Code })
                .IsUnique();
        });

        modelBuilder.Entity<CompanyMembershipRecord>(entity =>
        {
            entity.ToTable("CompanyMemberships");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.UserId });
            entity.Property(x => x.IsActive).HasDefaultValue(true);
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");

            entity.HasOne<CompanyRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId })
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<PlatformUserRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.UserId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RoleAssignmentRecord>(entity =>
        {
            entity.ToTable("RoleAssignments");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.UserId, x.RoleKey });
            entity.Property(x => x.RoleKey).HasMaxLength(100);
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");

            entity.HasOne<CompanyMembershipRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.UserId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskRecord>(entity =>
        {
            entity.ToTable("Tasks");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.Id });
            entity.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(40)
                .HasDefaultValue(TaskExecutionStatus.Pending);
            entity.Property(x => x.WaitReason).HasMaxLength(1000);
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");
            entity.Property(x => x.UpdatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");

            entity.HasOne<CompanyMembershipRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, UserId = x.CreatedByUserId })
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.Status });
        });

        modelBuilder.Entity<TaskStepRecord>(entity =>
        {
            entity.ToTable("TaskSteps");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.TaskId, x.Id });
            entity.Property(x => x.StepKey).HasMaxLength(200);
            entity.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(40)
                .HasDefaultValue(TaskStepStatus.Pending);
            entity.Property(x => x.Attempt).HasDefaultValue(0);
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");
            entity.Property(x => x.UpdatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");

            entity.HasOne<TaskRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.TaskId })
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepKey })
                .IsUnique();
        });

        modelBuilder.Entity<TaskDependencyRecord>(entity =>
        {
            entity.ToTable("TaskDependencies");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId, x.DependsOnStepId });

            entity.HasOne<TaskStepRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.TaskId, Id = x.StepId })
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne<TaskStepRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.TaskId, Id = x.DependsOnStepId })
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.TaskId, x.DependsOnStepId });
        });

        modelBuilder.Entity<TaskEventRecord>(entity =>
        {
            entity.ToTable("TaskEvents");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.TaskId, x.Sequence });
            entity.Property(x => x.EventType).HasMaxLength(100);
            entity.Property(x => x.OccurredAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");

            entity.HasOne<TaskRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.TaskId })
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<TaskStepRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.TaskId, Id = x.StepId })
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<TaskCheckpointRecord>(entity =>
        {
            entity.ToTable("TaskCheckpoints");
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId, x.Version });
            entity.Property(x => x.CreatedAtUtc)
                .HasDefaultValueSql("SYSDATETIMEOFFSET()");

            entity.HasOne<TaskStepRecord>()
                .WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.TaskId, Id = x.StepId })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
