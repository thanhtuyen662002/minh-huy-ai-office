namespace MinhHuy.AiOffice.Shared.Contracts.Erp;

public enum ErpSchemaObjectKind
{
    Table,
    View,
    StoredProcedure,
    Function,
    Trigger,
    ForeignKey,
    Index
}

public enum ErpSchemaChangeKind
{
    Additive,
    Compatible,
    Breaking,
    Unknown
}

public sealed record ErpSchemaObject(
    ErpSchemaObjectKind Kind,
    string Schema,
    string Name,
    string DefinitionHash);

public sealed record ErpSchemaSnapshot(
    string TenantId,
    string CompanyId,
    string DataSourceId,
    long Version,
    IReadOnlyList<ErpSchemaObject> Objects)
{
    public ErpSchemaSnapshot Validate()
    {
        RequireCanonical(TenantId, nameof(TenantId));
        RequireCanonical(CompanyId, nameof(CompanyId));
        RequireCanonical(DataSourceId, nameof(DataSourceId));
        if (Version <= 0) throw new ArgumentOutOfRangeException(nameof(Version));
        if (Objects is null) throw new ArgumentNullException(nameof(Objects));

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Objects)
        {
            RequireCanonical(item.Schema, nameof(item.Schema));
            RequireCanonical(item.Name, nameof(item.Name));
            RequireCanonical(item.DefinitionHash, nameof(item.DefinitionHash));
            var identity = $"{item.Kind}:{item.Schema}.{item.Name}";
            if (!identities.Add(identity))
                throw new InvalidOperationException($"Duplicate schema object: {identity}");
        }
        return this;
    }

    private static void RequireCanonical(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException($"{name} must be non-empty canonical text.", name);
    }
}

public sealed record ErpSchemaChange(
    ErpSchemaChangeKind Kind,
    ErpSchemaObjectKind ObjectKind,
    string Schema,
    string Name,
    string? PreviousDefinitionHash,
    string? CurrentDefinitionHash);

public static class ErpSchemaDiffer
{
    public static IReadOnlyList<ErpSchemaChange> Diff(ErpSchemaSnapshot previous, ErpSchemaSnapshot current)
    {
        previous.Validate();
        current.Validate();
        if (previous.TenantId != current.TenantId || previous.CompanyId != current.CompanyId || previous.DataSourceId != current.DataSourceId)
            throw new InvalidOperationException("Schema snapshots must have identical tenant/company/data-source authority.");
        if (current.Version <= previous.Version)
            throw new InvalidOperationException("Schema snapshot version must increase monotonically.");

        var oldMap = previous.Objects.ToDictionary(Key, StringComparer.Ordinal);
        var newMap = current.Objects.ToDictionary(Key, StringComparer.Ordinal);
        var keys = oldMap.Keys.Union(newMap.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
        var changes = new List<ErpSchemaChange>();
        foreach (var key in keys)
        {
            oldMap.TryGetValue(key, out var oldObject);
            newMap.TryGetValue(key, out var newObject);
            if (oldObject is null)
            {
                changes.Add(Change(ErpSchemaChangeKind.Additive, newObject!, null, newObject!.DefinitionHash));
            }
            else if (newObject is null)
            {
                changes.Add(Change(ErpSchemaChangeKind.Breaking, oldObject, oldObject.DefinitionHash, null));
            }
            else if (!StringComparer.Ordinal.Equals(oldObject.DefinitionHash, newObject.DefinitionHash))
            {
                changes.Add(Change(ErpSchemaChangeKind.Unknown, newObject, oldObject.DefinitionHash, newObject.DefinitionHash));
            }
        }
        return changes;
    }

    private static string Key(ErpSchemaObject value) => $"{value.Kind}:{value.Schema}.{value.Name}";
    private static ErpSchemaChange Change(ErpSchemaChangeKind kind, ErpSchemaObject value, string? previous, string? current) =>
        new(kind, value.Kind, value.Schema, value.Name, previous, current);
}
