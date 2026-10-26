@{
    # All default rules, with narrow exclusions for this existing operator tooling.
    ExcludeRules = @(
        # Existing manual scripts intentionally display non-secret operator guidance.
        'PSAvoidUsingWriteHost',
        # The wizard is the ShouldProcess boundary; internal actions/pure helpers
        # are never used as standalone dry-run entry points.
        'PSUseShouldProcessForStateChangingFunctions',
        # These scripts run in PowerShell 7, where UTF-8 without BOM is native.
        'PSUseBOMForUnicodeEncodedFile',
        # False positives: parameters consumed only by nested functions, closures
        # or runspace scriptblocks (e.g. LanPrefix, Secrets, Queue) are not seen.
        'PSReviewUnusedParameter'
    )
}
