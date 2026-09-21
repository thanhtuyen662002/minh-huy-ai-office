using MinhHuy.AIOffice.Platform.Persistence.Recovery;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class BackupArtifactContractTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Create_ProducesDeterministicUtcArtifactWithoutSecretMaterial()
    {
        var descriptor = CreateDescriptor();
        Assert.Equal("company01-AIOffice_Company01-20260921T033045Z-0123456789ab.bak", descriptor.ArtifactName);
        Assert.Equal("company01", descriptor.CompanyId);
        Assert.Equal(Hash, descriptor.Sha256);
        Assert.Equal("keyvault://backup/aioffice/company01/v3", descriptor.EncryptionKeyReference);
        Assert.Equal(TimeSpan.Zero, descriptor.CreatedAtUtc.Offset);
        Assert.DoesNotContain("Password", descriptor.ArtifactName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(descriptor.EncryptionKeyReference, descriptor.ArtifactName, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyForRestore_AcceptsCanonicalArtifactForExpectedCompanyDatabaseAndHash()
    {
        var descriptor = CreateDescriptor();
        Assert.True(BackupArtifactContract.VerifyForRestore(descriptor, "company01", "AIOffice_Company01", Hash.ToUpperInvariant()));
    }

    [Fact]
    public void VerifyForRestore_RejectsCrossCompanyRestoreCandidateEvenWhenDatabaseMatches()
    {
        var descriptor = CreateDescriptor();
        Assert.False(BackupArtifactContract.VerifyForRestore(descriptor, "company02", descriptor.DatabaseName, Hash));
    }

    [Fact]
    public void VerifyForRestore_RejectsCrossDatabaseRestoreCandidate()
    {
        var descriptor = CreateDescriptor();
        Assert.False(BackupArtifactContract.VerifyForRestore(descriptor, descriptor.CompanyId, "AIOffice_Company02", Hash));
    }

    [Fact]
    public void VerifyForRestore_RejectsTamperedContentHash()
    {
        var descriptor = CreateDescriptor();
        const string otherHash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        Assert.False(BackupArtifactContract.VerifyForRestore(descriptor, descriptor.CompanyId, descriptor.DatabaseName, otherHash));
    }

    [Theory]
    [InlineData("CompanyId", "company02")]
    [InlineData("DatabaseName", "AIOffice_Company02")]
    [InlineData("ArtifactName", "other.bak")]
    public void VerifyForRestore_RejectsTamperedManifestFields(string field, string value)
    {
        var descriptor = CreateDescriptor();
        descriptor = field switch
        {
            "CompanyId" => descriptor with { CompanyId = value },
            "DatabaseName" => descriptor with { DatabaseName = value },
            _ => descriptor with { ArtifactName = value }
        };
        Assert.False(BackupArtifactContract.VerifyForRestore(descriptor, "company01", "AIOffice_Company01", Hash));
    }

    [Fact]
    public void RecoveryReferences_AreCompanyScopedAndContainReferencesOnly()
    {
        var references = BackupArtifactContract.CreateRecoveryReferences("company01", "object://backups/company01/sql", "config://aioffice/company01/production", "keyvault://aioffice/company01/sql");
        Assert.True(BackupArtifactContract.VerifyRecoveryReferences(references, "company01"));
        Assert.False(BackupArtifactContract.VerifyRecoveryReferences(references, "company02"));
        Assert.DoesNotContain("Password", string.Join('|', references.ObjectStorageReference, references.ConfigurationReference, references.SecretReference), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Server=db;Password=secret")]
    [InlineData("Password=secret")]
    [InlineData("token=secret")]
    public void RecoveryReferences_RejectSecretLikeValues(string secretLikeValue)
    {
        Assert.Throws<ArgumentException>(() => BackupArtifactContract.CreateRecoveryReferences("company01", "object://backups/company01", "config://aioffice/company01", secretLikeValue));
    }

    [Fact]
    public void VerifyRecoveryDrill_AcceptsFreshOnlineRestoreForExpectedCompany()
    {
        var completedAt = new DateTimeOffset(2026, 9, 21, 6, 0, 0, TimeSpan.Zero);
        var evidence = new RecoveryDrillEvidence(CreateDescriptor(), completedAt, Hash, DatabaseOnline: true);
        Assert.True(BackupArtifactContract.VerifyRecoveryDrill(evidence, "company01", "AIOffice_Company01", completedAt.AddHours(2), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void VerifyRecoveryDrill_RejectsFailedIntegrityOrApplicationProbe()
    {
        var completedAt = new DateTimeOffset(2026, 9, 21, 6, 0, 0, TimeSpan.Zero);
        var evidence = new RecoveryDrillEvidence(CreateDescriptor(), completedAt, Hash, DatabaseOnline: true);
        Assert.False(BackupArtifactContract.VerifyRecoveryDrill(evidence with { IntegrityCheckPassed = false }, "company01", "AIOffice_Company01", completedAt.AddHours(1), TimeSpan.FromHours(24)));
        Assert.False(BackupArtifactContract.VerifyRecoveryDrill(evidence with { ApplicationProbePassed = false }, "company01", "AIOffice_Company01", completedAt.AddHours(1), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void VerifyRecoveryDrill_RejectsStaleOfflineFutureOrCrossCompanyEvidence()
    {
        var completedAt = new DateTimeOffset(2026, 9, 21, 6, 0, 0, TimeSpan.Zero);
        var evidence = new RecoveryDrillEvidence(CreateDescriptor(), completedAt, Hash, DatabaseOnline: true);
        Assert.False(BackupArtifactContract.VerifyRecoveryDrill(evidence, "company01", "AIOffice_Company01", completedAt.AddHours(25), TimeSpan.FromHours(24)));
        Assert.False(BackupArtifactContract.VerifyRecoveryDrill(evidence with { DatabaseOnline = false }, "company01", "AIOffice_Company01", completedAt.AddHours(1), TimeSpan.FromHours(24)));
        Assert.False(BackupArtifactContract.VerifyRecoveryDrill(evidence with { CompletedAtUtc = completedAt.AddMinutes(1) }, "company01", "AIOffice_Company01", completedAt, TimeSpan.FromHours(24)));
        Assert.False(BackupArtifactContract.VerifyRecoveryDrill(evidence, "company02", "AIOffice_Company01", completedAt.AddHours(1), TimeSpan.FromHours(24)));
    }

    [Theory]
    [InlineData(RecoveryFailureScenario.WorkerUnavailable)]
    [InlineData(RecoveryFailureScenario.ServerUnavailable)]
    [InlineData(RecoveryFailureScenario.ProviderUnavailable)]
    [InlineData(RecoveryFailureScenario.QueueUnavailable)]
    [InlineData(RecoveryFailureScenario.DatabaseUnavailable)]
    public void VerifyFailureDrill_AcceptsRecoveredInfrastructureScenario(RecoveryFailureScenario scenario)
    {
        var startedAt = new DateTimeOffset(2026, 9, 21, 7, 0, 0, TimeSpan.Zero);
        var evidence = new RecoveryFailureDrillEvidence("company01", scenario, startedAt, startedAt.AddMinutes(8), true, true, true);
        Assert.True(BackupArtifactContract.VerifyFailureDrill(evidence, "company01", TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void VerifyFailureDrill_FailsClosedForScopeRecoveryIntegrityIsolationOrRtoFailure()
    {
        var startedAt = new DateTimeOffset(2026, 9, 21, 7, 0, 0, TimeSpan.Zero);
        var evidence = new RecoveryFailureDrillEvidence("company01", RecoveryFailureScenario.DatabaseUnavailable, startedAt, startedAt.AddMinutes(8), true, true, true);
        Assert.False(BackupArtifactContract.VerifyFailureDrill(evidence, "company02", TimeSpan.FromMinutes(15)));
        Assert.False(BackupArtifactContract.VerifyFailureDrill(evidence with { WorkloadRecovered = false }, "company01", TimeSpan.FromMinutes(15)));
        Assert.False(BackupArtifactContract.VerifyFailureDrill(evidence with { DataIntegrityVerified = false }, "company01", TimeSpan.FromMinutes(15)));
        Assert.False(BackupArtifactContract.VerifyFailureDrill(evidence with { TenantIsolationVerified = false }, "company01", TimeSpan.FromMinutes(15)));
        Assert.False(BackupArtifactContract.VerifyFailureDrill(evidence with { RecoveredAtUtc = startedAt.AddMinutes(16) }, "company01", TimeSpan.FromMinutes(15)));
        Assert.False(BackupArtifactContract.VerifyFailureDrill(evidence with { RecoveredAtUtc = startedAt.AddSeconds(-1) }, "company01", TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void VerifyRepeatedFailureDrills_RequiresFreshCoverageOfEveryScenario()
    {
        var now = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);
        var evidence = Enum.GetValues<RecoveryFailureScenario>()
            .Select((scenario, index) => new RecoveryFailureDrillEvidence("company01", scenario, now.AddMinutes(-20 - index), now.AddMinutes(-12 - index), true, true, true))
            .ToArray();

        Assert.True(BackupArtifactContract.VerifyRepeatedFailureDrills(evidence, "company01", now, TimeSpan.FromHours(24), TimeSpan.FromMinutes(15)));
        Assert.False(BackupArtifactContract.VerifyRepeatedFailureDrills(evidence.Where(item => item.Scenario != RecoveryFailureScenario.QueueUnavailable), "company01", now, TimeSpan.FromHours(24), TimeSpan.FromMinutes(15)));
        Assert.False(BackupArtifactContract.VerifyRepeatedFailureDrills(evidence.Select(item => item.Scenario == RecoveryFailureScenario.ServerUnavailable ? item with { RecoveredAtUtc = now.AddHours(-25) } : item), "company01", now, TimeSpan.FromHours(24), TimeSpan.FromMinutes(15)));
        Assert.False(BackupArtifactContract.VerifyRepeatedFailureDrills(evidence.Select(item => item.Scenario == RecoveryFailureScenario.ProviderUnavailable ? item with { CompanyId = "company02" } : item), "company01", now, TimeSpan.FromHours(24), TimeSpan.FromMinutes(15)));
    }

    [Theory]
    [InlineData("AIOffice;DROP DATABASE master")]
    [InlineData("../AIOffice")]
    [InlineData("AIOffice Company")]
    public void Create_RejectsUnsafeDatabaseNames(string databaseName)
    {
        Assert.Throws<ArgumentException>(() => BackupArtifactContract.Create("company01", databaseName, DateTimeOffset.UtcNow, Hash, "keyvault://backup/key"));
    }

    [Theory]
    [InlineData("company;DROP")]
    [InlineData("../company")]
    [InlineData("company 01")]
    public void Create_RejectsUnsafeCompanyIds(string companyId)
    {
        Assert.Throws<ArgumentException>(() => BackupArtifactContract.Create(companyId, "AIOffice", DateTimeOffset.UtcNow, Hash, "keyvault://backup/key"));
    }

    [Theory]
    [InlineData("Server=db;Password=secret")]
    [InlineData("secret=value")]
    public void Create_RejectsSecretLikeEncryptionKeyValues(string secretLikeValue)
    {
        Assert.Throws<ArgumentException>(() => BackupArtifactContract.Create("company01", "AIOffice", DateTimeOffset.UtcNow, Hash, secretLikeValue));
    }

    [Theory]
    [InlineData("not-a-sha256")]
    [InlineData("")]
    public void Create_RejectsInvalidIntegrityHash(string invalidHash)
    {
        Assert.Throws<ArgumentException>(() => BackupArtifactContract.Create("company01", "AIOffice", DateTimeOffset.UtcNow, invalidHash, "keyvault://backup/key"));
    }

    private static BackupArtifactDescriptor CreateDescriptor()
        => BackupArtifactContract.Create("company01", "AIOffice_Company01", new DateTimeOffset(2026, 9, 21, 10, 30, 45, TimeSpan.FromHours(7)), Hash.ToUpperInvariant(), "keyvault://backup/aioffice/company01/v3");
}
