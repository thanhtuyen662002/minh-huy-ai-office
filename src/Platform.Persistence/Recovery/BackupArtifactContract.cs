namespace MinhHuy.AIOffice.Platform.Persistence.Recovery;

public sealed record BackupArtifactDescriptor(
    string CompanyId,
    string DatabaseName,
    string ArtifactName,
    DateTimeOffset CreatedAtUtc,
    string Sha256,
    string EncryptionKeyReference);

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
            throw new ArgumentException("Encryption key must be an opaque reference, never secret material.", nameof(value));
        }

        return value;
    }
}
