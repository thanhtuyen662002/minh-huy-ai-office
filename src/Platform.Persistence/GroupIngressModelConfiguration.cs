using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal static class GroupIngressModelConfiguration
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";

    internal static void ConfigureGroupIngress(this ModelBuilder model)
    {
        model.Entity<GroupConnectorAccountRecord>(entity =>
        {
            Company(entity, "GroupConnectorAccounts", "Id");
            entity.ToTable("GroupConnectorAccounts", table =>
            {
                table.HasCheckConstraint("CK_GroupConnectorAccounts_Version", "[Version] > 0");
                table.HasCheckConstraint("CK_GroupConnectorAccounts_Qualification", "ISJSON([QualificationJson]) = 1");
            });
            entity.Property(x => x.Provider).HasMaxLength(64).IsUnicode(false).UseCollation(BinaryCollation);
            Opaque(entity, "ExternalAccountId"); Hash(entity, "IdentityHash");
            entity.Property(x => x.PackageVersion).HasMaxLength(64).IsUnicode(false);
            entity.Property(x => x.GitCommit).HasMaxLength(40).IsUnicode(false);
            entity.Property(x => x.QualificationJson).HasMaxLength(16000);
            entity.Property(x => x.IsEnabled).HasDefaultValue(false);
            entity.HasIndex(x => x.IdentityHash).IsUnique();
        });
        model.Entity<GroupServiceRecord>(entity =>
        {
            Company(entity, "GroupServices", "Id");
            entity.ToTable("GroupServices", table => table.HasCheckConstraint("CK_GroupServices_Epoch", "[CredentialEpoch] > 0"));
            entity.Property(x => x.CredentialReference).HasMaxLength(512).UseCollation(BinaryCollation);
            entity.Property(x => x.IsEnabled).HasDefaultValue(false);
        });
        model.Entity<GroupBindingRecord>(entity =>
        {
            Company(entity, "GroupBindings", "Id");
            entity.ToTable("GroupBindings", table =>
            {
                table.HasCheckConstraint("CK_GroupBindings_Role", "[Role] IN (1,2)");
                table.HasCheckConstraint("CK_GroupBindings_Versions", "[Version] > 0 AND [DeletionGeneration] >= 0");
            });
            entity.Property(x => x.Provider).HasMaxLength(64).IsUnicode(false).UseCollation(BinaryCollation);
            Opaque(entity, "ExternalAccountId"); Opaque(entity, "ExternalGroupId");
            Hash(entity, "IdentityHash"); Hash(entity, "PhysicalGroupHash");
            entity.Property(x => x.DisplayName).HasMaxLength(200);
            entity.Property(x => x.IsEnabled).HasDefaultValue(false);
            entity.HasIndex(x => x.PhysicalGroupHash).IsUnique();
            entity.HasIndex(x => x.IdentityHash).IsUnique();
            entity.HasOne<GroupConnectorAccountRecord>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ConnectorAccountId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupServiceGrantRecord>(entity =>
        {
            Company(entity, "GroupServiceGrants", "ServiceId", "BindingId", "Capability");
            Binding(entity);
            entity.ToTable("GroupServiceGrants", table => table.HasCheckConstraint("CK_GroupServiceGrants_Values", "[Version] > 0 AND [Capability] IN (1,2,3)"));
            entity.Property(x => x.IsEnabled).HasDefaultValue(false);
            entity.HasOne<GroupServiceRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupReaderGrantRecord>(entity =>
        {
            Company(entity, "GroupReaderGrants", "UserId", "BindingId");
            Binding(entity);
            entity.ToTable("GroupReaderGrants", table => table.HasCheckConstraint("CK_GroupReaderGrants_Version", "[Version] > 0"));
            entity.Property(x => x.IsEnabled).HasDefaultValue(false);
            entity.HasOne<CompanyMembershipRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, x.UserId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupListenerLeaseRecord>(entity =>
        {
            Company(entity, "GroupListenerLeases", "ConnectorAccountId");
            entity.ToTable("GroupListenerLeases", table => table.HasCheckConstraint("CK_GroupListenerLeases_Values",
                "[Epoch] > 0 AND [ExpiresAtUtc] >= [HeartbeatAtUtc] AND DATEPART(tz,[ExpiresAtUtc]) = 0 AND DATEPART(tz,[HeartbeatAtUtc]) = 0"));
            entity.HasOne<GroupConnectorAccountRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ConnectorAccountId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupAccountCoverageGapRecord>(entity =>
        {
            Company(entity, "GroupAccountCoverageGaps", "ConnectorAccountId", "ListenerEpoch", "Reason");
            entity.ToTable("GroupAccountCoverageGaps", table => table.HasCheckConstraint("CK_GroupAccountCoverageGaps_Values",
                "[ListenerEpoch] > 0 AND [Reason] IN ('listener-started','listener-expired','listener-stopped') AND [RecordedAtUtc] >= [OpenedAtUtc] AND DATEPART(tz,[OpenedAtUtc]) = 0 AND DATEPART(tz,[RecordedAtUtc]) = 0"));
            entity.Property(x => x.Reason).HasMaxLength(32).IsUnicode(false).UseCollation(BinaryCollation);
            entity.HasOne<GroupConnectorAccountRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ConnectorAccountId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupListenerCommandReceiptRecord>(entity =>
        {
            Company(entity, "GroupListenerCommandReceipts", "ServiceId", "CredentialEpoch", "Nonce");
            entity.ToTable("GroupListenerCommandReceipts", table => table.HasCheckConstraint("CK_GroupListenerCommandReceipts_Values",
                "[CredentialEpoch] > 0 AND [ListenerEpoch] > 0 AND [Operation] IN (1,2,3) AND [ExpiresAtUtc] >= [HeartbeatAtUtc] AND [CommittedAtUtc] >= [HeartbeatAtUtc] AND DATEPART(tz,[HeartbeatAtUtc]) = 0 AND DATEPART(tz,[ExpiresAtUtc]) = 0 AND DATEPART(tz,[CommittedAtUtc]) = 0"));
            Hash(entity, "CommandSha256");
            entity.HasOne<GroupConnectorAccountRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ConnectorAccountId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GroupServiceRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupSourceStateRecord>(entity =>
        {
            Company(entity, "GroupSourceStates", "BindingId"); Binding(entity);
            entity.ToTable("GroupSourceStates", table =>
            {
                table.HasCheckConstraint("CK_GroupSourceStates_Cursors", "[CommittedSequence] >= [ScheduledThroughSequence] AND [ScheduledThroughSequence] >= 0");
                table.HasCheckConstraint("CK_GroupSourceStates_Pending", "([FirstPendingAtUtc] IS NULL AND [LastPendingAtUtc] IS NULL) OR ([FirstPendingAtUtc] IS NOT NULL AND [LastPendingAtUtc] IS NOT NULL AND [FirstPendingAtUtc] <= [LastPendingAtUtc] AND DATEPART(tz,[FirstPendingAtUtc]) = 0 AND DATEPART(tz,[LastPendingAtUtc]) = 0)");
            });
        });
        model.Entity<GroupMessageRecord>(entity =>
        {
            Company(entity, "GroupMessages", "BindingId", "Id"); Binding(entity);
            Opaque(entity, "ExternalMessageId"); Hash(entity, "IdentityHash");
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.IdentityHash }).IsUnique();
        });
        model.Entity<GroupMessageRevisionRecord>(entity =>
        {
            Company(entity, "GroupMessageRevisions", "BindingId", "MessageId", "Revision"); Message(entity);
            entity.ToTable("GroupMessageRevisions", table =>
            {
                table.HasCheckConstraint("CK_GroupMessageRevisions_Values", "[Revision] > 0 AND [CommittedSequence] > 0 AND [SourceVersion] > 0 AND [DeletionGeneration] >= 0 AND [Kind] IN (1,2,3,4)");
                table.HasCheckConstraint("CK_GroupMessageRevisions_Content", "DATALENGTH([ProtectedContent]) BETWEEN 29 AND 65536");
                table.HasCheckConstraint("CK_GroupMessageRevisions_Utc", "DATEPART(tz,[OccurredAtUtc]) = 0 AND DATEPART(tz,[CommittedAtUtc]) = 0");
            });
            Opaque(entity, "ExternalRevisionEventId"); Opaque(entity, "SenderId"); Opaque(entity, "ReplyToMessageId");
            Hash(entity, "ContentSha256"); entity.Property(x => x.ProtectedContent).HasMaxLength(65536);
            entity.Property(x => x.ContentKeyId).HasMaxLength(64).IsUnicode(false).UseCollation(BinaryCollation);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.CommittedSequence }).IsUnique();
        });
        model.Entity<GroupIngressReceiptRecord>(entity =>
        {
            Company(entity, "GroupIngressReceipts", "BindingId", "EventIdentityHash"); Revision(entity);
            Hash(entity, "EventIdentityHash"); Hash(entity, "EnvelopeSha256"); Opaque(entity, "ExternalRevisionEventId");
            entity.ToTable("GroupIngressReceipts", table => table.HasCheckConstraint("CK_GroupIngressReceipts_Values", "[Revision] > 0 AND [CredentialEpoch] > 0 AND [ListenerEpoch] > 0 AND DATEPART(tz,[CommittedAtUtc]) = 0"));
            entity.HasOne<GroupServiceRecord>().WithMany().HasForeignKey(x => new { x.TenantId, x.CompanyId, Id = x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<GroupCoverageGapRecord>(entity =>
        {
            Company(entity, "GroupCoverageGaps", "BindingId", "Id"); Binding(entity);
            entity.ToTable("GroupCoverageGaps", table => table.HasCheckConstraint("CK_GroupCoverageGaps_Values", "[AfterCommittedSequence] >= 0 AND DATEPART(tz,[OpenedAtUtc]) = 0 AND ([ReconnectedAtUtc] IS NULL OR ([ReconnectedAtUtc] >= [OpenedAtUtc] AND DATEPART(tz,[ReconnectedAtUtc]) = 0))"));
            entity.Property(x => x.Reason).HasMaxLength(64).IsUnicode(false);
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.OpenedAtUtc });
        });
        model.Entity<GroupIngressOutboxRecord>(entity =>
        {
            Company(entity, "GroupIngressOutbox", "BindingId", "Id"); Revision(entity);
            entity.ToTable("GroupIngressOutbox", table => table.HasCheckConstraint("CK_GroupIngressOutbox_Values", "[Revision] > 0 AND [CommittedSequence] > 0 AND [PublishAttempts] >= 0 AND DATEPART(tz,[AvailableAtUtc]) = 0 AND ([PublishedAtUtc] IS NULL OR DATEPART(tz,[PublishedAtUtc]) = 0)"));
            entity.HasIndex(x => new { x.TenantId, x.CompanyId, x.BindingId, x.MessageId, x.Revision }).IsUnique();
            entity.HasIndex(x => new { x.PublishedAtUtc, x.AvailableAtUtc });
        });
    }

    private static void Company<T>(EntityTypeBuilder<T> entity, string table, params string[] key) where T : class
    {
        entity.ToTable(table);
        entity.HasKey(new[] { "TenantId", "CompanyId" }.Concat(key).ToArray());
        entity.HasOne<CompanyRecord>().WithMany().HasForeignKey("TenantId", "CompanyId").OnDelete(DeleteBehavior.Restrict);
    }

    private static void Binding<T>(EntityTypeBuilder<T> entity) where T : class =>
        entity.HasOne<GroupBindingRecord>().WithMany().HasForeignKey("TenantId", "CompanyId", "BindingId").OnDelete(DeleteBehavior.Restrict);

    private static void Message<T>(EntityTypeBuilder<T> entity) where T : class =>
        entity.HasOne<GroupMessageRecord>().WithMany().HasForeignKey("TenantId", "CompanyId", "BindingId", "MessageId").OnDelete(DeleteBehavior.Restrict);

    private static void Revision<T>(EntityTypeBuilder<T> entity) where T : class =>
        entity.HasOne<GroupMessageRevisionRecord>().WithMany().HasForeignKey("TenantId", "CompanyId", "BindingId", "MessageId", "Revision").OnDelete(DeleteBehavior.Restrict);

    private static void Opaque<T>(EntityTypeBuilder<T> entity, string property) where T : class =>
        entity.Property<string>(property).HasMaxLength(256).UseCollation(BinaryCollation);

    private static void Hash<T>(EntityTypeBuilder<T> entity, string property) where T : class =>
        entity.Property<string>(property).HasMaxLength(64).IsFixedLength().IsUnicode(false).UseCollation(BinaryCollation);
}
