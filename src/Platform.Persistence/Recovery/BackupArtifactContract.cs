namespace MinhHuy.AIOffice.Platform.Persistence.Recovery;

public sealed record BackupArtifactDescriptor(
    string CompanyId,
    string DatabaseName,
    string ArtifactName,
    DateTimeOffset CreatedAtUtc,
    string Sha256,
    string EncryptionKeyReference);

public sealed record RecoveryDrillEvidence(
    BackupArtifactDescriptor Artifact,
    DateTimeOffset CompletedAtUtc,
    string RestoredSha256,
    bool DatabaseOnline,
    bool IntegrityCheckPassed = true,
    bool ApplicationProbePassed = true);

public sealed record RecoveryReferenceSet(
    string CompanyId,
    string ObjectStorageReference,
    string ConfigurationReference,
    string SecretReference);

public static class BackupArtifactContract
{
    public static BackupArtifactDescriptor Create(
        string companyId,
        string databaseName,
        DateTimeOffset createdAt,
        string sha256,
        string encryptionKeyReference)
    {
        var safeCompanyId = RequireSafeToken(companyId, nameof(companyId));
        var safeDatabaseName = RequireSafeToken(databaseName, nameof(databaseName));
        var normalizedHash = RequireSha256(sha256);
        var keyReference = RequireReference(encryptionKeyReference);
        var utc = createdAt.ToUniversalTime();
        var artifactName = $"{safeCompanyId}-{safeDatabaseName}-{utc:yyyyMMddTHHmmssZ}-{normalizedHash[..12]}.bak";

        return new BackupArtifactDescriptor(safeCompanyId, safeDatabaseName, artifactName, utc, normalizedHash, keyReference);
    }

    public static RecoveryReferenceSet CreateRecoveryReferences(
        string companyId,
        string objectStorageReference,
        string configurationReference,
        string secretReference)
        => new(
            RequireSafeToken(companyId, nameof(companyId)),
            RequireReference(objectStorageReference),
            RequireReference(configurationReference),
            RequireReference(secretReference));

    public static bool VerifyRecoveryReferences(RecoveryReferenceSet references, string expectedCompanyId)
    {
        ArgumentNullException.ThrowIfNull(references);
        var expectedCompany = RequireSafeToken(expectedCompanyId, nameof(expectedCompanyId));
        var canonical = CreateRecoveryReferences(
            references.CompanyId,
            references.ObjectStorageReference,
            references.ConfigurationReference,
            references.SecretReference);

        return string.Equals(references.CompanyId, expectedCompany, StringComparison.Ordinal)
            && references == canonical;
    }

    public static bool VerifyForRestore(
        BackupArtifactDescriptor descriptor,
        string expectedCompanyId,
        string expectedDatabaseName,
        string observedSha256)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var expectedCompany = RequireSafeToken(expectedCompanyId, nameof(expectedCompanyId));
        var expectedDatabase = RequireSafeToken(expectedDatabaseName, nameof(expectedDatabaseName));
        var observedHash = RequireSha256(observedSha256);
        var canonical = Create(
            descriptor.CompanyId,
            descriptor.DatabaseName,
            descriptor.CreatedAtUtc,
            descriptor.Sha256,
            descriptor.EncryptionKeyReference);

        return string.Equals(descriptor.CompanyId, expectedCompany, StringComparison.Ordinal)
            && string.Equals(descriptor.DatabaseName, expectedDatabase, StringComparison.Ordinal)
            && string.Equals(descriptor.Sha256, observedHash, StringComparison.Ordinal)
            && descriptor == canonical;
    }

    public static bool VerifyRecoveryDrill(
        RecoveryDrillEvidence evidence,
        string expectedCompanyId,
        string expectedDatabaseName,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "Recovery drill maximum age must be positive.");
        }

        var completedAtUtc = evidence.CompletedAtUtc.ToUniversalTime();
        var nowUtc = now.ToUniversalTime();
        if (!evidence.DatabaseOnline
            || !evidence.IntegrityCheckPassed
            || !evidence.ApplicationProbePassed
            || completedAtUtc > nowUtc
            || nowUtc - completedAtUtc > maximumAge)
        {
            return false;
        }

        return VerifyForRestore(
            evidence.Artifact,
            expectedCompanyId,
            expectedDatabaseName,
            evidence.RestoredSha256);
    }

    private static string RequireSafeToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
        {
            throw new ArgumentException("Value must contain only letters, digits, '-' or '_'.", parameterName);
        }

        return value;
    }

    private static string RequireSha256(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || value.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new ArgumentException("SHA-256 must be exactly 64 hexadecimal characters.", nameof(value));
        }

        return value.ToLowerInvariant();
    }

    private static string RequireReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('=') || value.Contains(';'))
        {
            throw new ArgumentException("Recovery metadata must be an opaque reference, never secret material.", nameof(value));
        }

        return value;
    }
}
