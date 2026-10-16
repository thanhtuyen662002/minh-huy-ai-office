using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupModelAttemptAdmissionModelConfiguration
{
    internal static void ConfigureGroupModelAttemptAdmissions(this ModelBuilder model)
    {
        // Expand keys of immutable principals without rewriting any row.
        // Claims/times are read from the authentic start; original claim is
        // read from the authentic sidecar, never duplicated as free metadata.
        model.Entity<GroupModelAttemptStartRecord>().HasAlternateKey(x => new
        { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.BatchId, x.InputSha256 })
            .HasName("AK_GroupModelAttemptStarts_Admission");
        model.Entity<GroupOriginalAdmissionSidecarRecord>().HasAlternateKey(x => new
        { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256, x.SidecarSha256 })
            .HasName("AK_GroupOriginalAdmissionSidecars_Admission");
        // Preserve the existing plan lookup index during this expand step.
        model.Entity<GroupOriginalAdmissionSidecarRecord>().HasIndex(x => new
        { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256 });
        model.Entity<GroupModelAttemptAdmissionRecord>(entity =>
        {
            entity.ToTable("GroupModelAttemptAdmissions", table =>
            {
                table.HasCheckConstraint("CK_GroupModelAttemptAdmissions_Format",
                    "[FormatVersion]=1 AND DATALENGTH([Snapshot]) BETWEEN 256 AND 10000");
                table.HasCheckConstraint("CK_GroupModelAttemptAdmissions_Chunk",
                    "[ChunkOrdinal] BETWEEN 1 AND 100 AND [ChunkOperationId]<>'00000000-0000-0000-0000-000000000000'");
                table.HasCheckConstraint("CK_GroupModelAttemptAdmissions_Identity", string.Join(" AND ",
                    new[] { "InputSha256", "PlanSha256", "SidecarSha256", "SnapshotSha256" }.Select(name =>
                        $"DATALENGTH([{name}])=64 AND [{name}] NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2")));
                table.HasCheckConstraint("CK_GroupModelAttemptAdmissions_Time",
                    "DATEPART(tz,[PreparedAtUtc])=0 AND DATEPART(tz,[ExpiresAtUtc])=0 AND [ExpiresAtUtc]>[PreparedAtUtc] AND [ExpiresAtUtc]<=DATEADD(second,120,[PreparedAtUtc])");
            });
            entity.HasKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId });
            foreach (var name in new[] { "InputSha256", "PlanSha256", "SidecarSha256", "SnapshotSha256" })
                entity.Property<string>(name).HasMaxLength(64).IsFixedLength().IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
            entity.Property(x => x.Snapshot).HasMaxLength(GroupStructuredAdmissionSnapshotCodec.MaximumBytes);
            entity.HasOne<GroupModelAttemptStartRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.BatchId, x.InputSha256 })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.RequestId, x.BatchId, x.InputSha256 })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupModelAttemptAdmissions_ExactStart");
            entity.HasOne<GroupOriginalAdmissionSidecarRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256, x.SidecarSha256 })
                .HasPrincipalKey(x => new { x.TenantId, x.CompanyId, x.BindingId, x.BatchId, x.PlanSha256, x.SidecarSha256 })
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_GroupModelAttemptAdmissions_ExactOriginalAdmission");
        });
    }
}
