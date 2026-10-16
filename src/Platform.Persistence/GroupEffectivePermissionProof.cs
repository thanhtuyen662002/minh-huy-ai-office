namespace MinhHuy.AIOffice.Platform.Persistence;

// Fixed server-owned scalar proofs only. This composes SQL text, never a
// permission verdict. Every caller still executes a fresh actual connection
// check at each original fence; no result survives a key wait or another unit.
internal static class GroupEffectivePermissionProof
{
    internal static string All(params string[] proofs)
    {
        ArgumentNullException.ThrowIfNull(proofs);
        if (proofs.Length is < 1 or > 6 || proofs.Any(proof => string.IsNullOrWhiteSpace(proof)))
            throw new ArgumentException("Fixed effective permission proofs are required.", nameof(proofs));
        // Inputs are the existing immutable VerificationSql properties, not
        // source, HTTP, provider or model-selected fragments. A scalar NULL or
        // any value other than integer1 makes the complete CASE return0.
        return "SELECT CASE WHEN\n" + string.Join("\n AND ", proofs.Select(proof =>
            "(" + proof.Trim().TrimEnd(';') + ")=1")) + "\n THEN 1 ELSE 0 END;";
    }
}
