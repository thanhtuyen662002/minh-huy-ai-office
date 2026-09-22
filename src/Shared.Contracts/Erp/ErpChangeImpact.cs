namespace MinhHuy.AiOffice.Shared.Contracts.Erp;

public enum ErpCompatibilityAction
{
    None,
    Review,
    MigrationRequired
}

public sealed record ErpChangedObject(
    ErpCatalogItemKind Kind,
    string Key,
    string PreviousVersion,
    string CurrentVersion)
{
    public ErpChangedObject Validate()
    {
        RequireCanonical(Key, nameof(Key));
        RequireCanonical(PreviousVersion, nameof(PreviousVersion));
        RequireCanonical(CurrentVersion, nameof(CurrentVersion));
        return this;
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
        }
    }
}

public sealed record ErpImpactBinding(
    ErpCatalogItemKind Kind,
    string ObjectKey,
    IReadOnlyList<string> FeatureKeys,
    IReadOnlyList<string> CapabilityKeys,
    IReadOnlyList<string> SkillKeys,
    IReadOnlyList<string> WorkflowKeys)
{
    public ErpImpactBinding Validate()
    {
        RequireCanonical(ObjectKey, nameof(ObjectKey));
        ValidateKeys(FeatureKeys, nameof(FeatureKeys));
        ValidateKeys(CapabilityKeys, nameof(CapabilityKeys));
        ValidateKeys(SkillKeys, nameof(SkillKeys));
        ValidateKeys(WorkflowKeys, nameof(WorkflowKeys));
        return this;
    }

    private static void ValidateKeys(IReadOnlyList<string> values, string name)
    {
        ArgumentNullException.ThrowIfNull(values);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            RequireCanonical(value, name);
            if (!seen.Add(value))
            {
                throw new InvalidOperationException($"{name} keys must be unique.");
            }
        }
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
        }
    }
}

public sealed record ErpChangeImpact(
    ErpChangedObject Change,
    IReadOnlyList<string> AffectedFeatures,
    IReadOnlyList<string> AffectedCapabilities,
    IReadOnlyList<string> AffectedSkills,
    IReadOnlyList<string> AffectedWorkflows,
    ErpCompatibilityAction RequiredAction);

public sealed class ErpChangeImpactAnalyzer
{
    public IReadOnlyList<ErpChangeImpact> Analyze(
        ErpCatalog catalog,
        string tenantId,
        string companyId,
        string dataSourceId,
        IReadOnlyList<ErpChangedObject> changes,
        IReadOnlyList<ErpImpactBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(bindings);
        catalog.Validate();
        catalog.AssertAuthority(tenantId, companyId, dataSourceId);

        var bindingIndex = bindings
            .Select(binding => binding.Validate())
            .ToDictionary(binding => (binding.Kind, binding.ObjectKey));

        var results = new List<ErpChangeImpact>(changes.Count);
        foreach (var change in changes)
        {
            change.Validate();
            if (!bindingIndex.TryGetValue((change.Kind, change.Key), out var binding))
            {
                throw new InvalidOperationException($"No compatibility binding exists for changed ERP object {change.Kind}:{change.Key}.");
            }

            var affectedFeatures = binding.FeatureKeys.ToArray();
            var affectedCapabilities = binding.CapabilityKeys.ToArray();
            ValidateCatalogReferences(catalog, affectedFeatures, affectedCapabilities);

            var action = StringComparer.Ordinal.Equals(change.PreviousVersion, change.CurrentVersion)
                ? ErpCompatibilityAction.None
                : affectedFeatures.Length + affectedCapabilities.Length + binding.SkillKeys.Count + binding.WorkflowKeys.Count == 0
                    ? ErpCompatibilityAction.Review
                    : ErpCompatibilityAction.MigrationRequired;

            results.Add(new ErpChangeImpact(
                change,
                affectedFeatures,
                affectedCapabilities,
                binding.SkillKeys.ToArray(),
                binding.WorkflowKeys.ToArray(),
                action));
        }

        return results;
    }

    private static void ValidateCatalogReferences(
        ErpCatalog catalog,
        IReadOnlyList<string> featureKeys,
        IReadOnlyList<string> capabilityKeys)
    {
        var features = catalog.Features.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var capabilities = catalog.Capabilities.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in featureKeys)
        {
            if (!features.Contains(key))
            {
                throw new InvalidOperationException($"Impact binding references unavailable ERP feature: {key}");
            }
        }

        foreach (var key in capabilityKeys)
        {
            if (!capabilities.Contains(key))
            {
                throw new InvalidOperationException($"Impact binding references unavailable ERP capability: {key}");
            }
        }
    }
}
