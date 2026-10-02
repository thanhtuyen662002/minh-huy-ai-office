using System.Globalization;

namespace MinhHuy.AiOffice.Shared.Contracts.Erp;

public enum ErpReadParameterKind
{
    Text,
    Int64,
    Decimal,
    Date,
    DateTimeOffset,
    Boolean,
    Guid
}

public sealed record ErpReadParameterValue(
    ErpReadParameterKind Kind,
    string Value)
{
    public const int MaximumTextLength = 512;

    public ErpReadParameterValue Validate()
    {
        if (string.IsNullOrWhiteSpace(Value)
            || !string.Equals(Value, Value.Trim(), StringComparison.Ordinal)
            || Value.Length > MaximumTextLength
            || Value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"ERP read parameter values must be canonical text of at most {MaximumTextLength} characters.",
                nameof(Value));
        }

        var valid = Kind switch
        {
            ErpReadParameterKind.Text => true,
            ErpReadParameterKind.Int64 => long.TryParse(
                Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _),
            ErpReadParameterKind.Decimal => decimal.TryParse(
                Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out _),
            ErpReadParameterKind.Date => DateOnly.TryParseExact(
                Value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _),
            ErpReadParameterKind.DateTimeOffset => DateTimeOffset.TryParseExact(
                Value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _),
            ErpReadParameterKind.Boolean => Value is "true" or "false",
            ErpReadParameterKind.Guid => System.Guid.TryParseExact(Value, "D", out _),
            _ => false
        };

        if (!valid)
        {
            throw new ArgumentException(
                $"ERP read parameter value is invalid for kind {Kind}.",
                nameof(Value));
        }

        return this;
    }
}

public sealed record ErpReadCapabilityRequest(
    Guid TenantId,
    Guid CompanyId,
    Guid DataSourceId,
    string CapabilityKey,
    string CapabilityVersion,
    IReadOnlyDictionary<string, ErpReadParameterValue> Parameters)
{
    public ErpReadCapabilityRequest Validate()
    {
        if (TenantId == Guid.Empty || CompanyId == Guid.Empty || DataSourceId == Guid.Empty)
        {
            throw new ArgumentException(
                "Tenant, company and data-source authority are required for ERP read capabilities.");
        }

        RequireCanonicalCapability(CapabilityKey, nameof(CapabilityKey));
        RequireCanonicalVersion(CapabilityVersion, nameof(CapabilityVersion));
        ArgumentNullException.ThrowIfNull(Parameters);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in Parameters)
        {
            RequireParameterName(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            pair.Value.Validate();

            if (!seen.Add(pair.Key))
            {
                throw new InvalidOperationException("ERP read parameter names must be unique.");
            }
        }

        return this;
    }

    internal static void RequireCanonicalCapability(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > 120
            || value.Any(char.IsControl)
            || value.Any(character =>
                !(char.IsLower(character)
                  || char.IsDigit(character)
                  || character is '.' or '-' or '_')))
        {
            throw new ArgumentException(
                "ERP capability keys must be lowercase canonical identifiers.",
                name);
        }
    }

    internal static void RequireCanonicalVersion(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > 40
            || value.Any(char.IsControl))
        {
            throw new ArgumentException("ERP capability version must be canonical text.", name);
        }
    }

    internal static void RequireParameterName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 64
            || !char.IsLower(value[0])
            || value.Any(character =>
                !(char.IsLower(character)
                  || char.IsDigit(character)
                  || character == '_')))
        {
            throw new ArgumentException(
                "ERP read parameter names must be lowercase identifiers.",
                nameof(value));
        }
    }
}

public sealed record ErpReadEvidenceRow(IReadOnlyList<string?> Values)
{
    public ErpReadEvidenceRow Validate(int columnCount)
    {
        ArgumentNullException.ThrowIfNull(Values);
        if (Values.Count != columnCount)
        {
            throw new InvalidOperationException(
                "ERP read evidence row width does not match the declared columns.");
        }

        foreach (var value in Values)
        {
            if (value is not null
                && (value.Length > ErpReadCapabilityEvidence.MaximumCellCharacters
                    || value.Any(char.IsControl)))
            {
                throw new InvalidOperationException(
                    "ERP read evidence contains an invalid or oversized cell value.");
            }
        }

        return this;
    }
}

public sealed record ErpReadCapabilityEvidence(
    Guid TenantId,
    Guid CompanyId,
    Guid DataSourceId,
    string CapabilityKey,
    string CapabilityVersion,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ErpReadEvidenceRow> Rows,
    bool RowsTruncated,
    bool ValuesTruncated,
    int EvidenceCharacters)
{
    public const int MaximumRows = 100;
    public const int MaximumColumns = 32;
    public const int MaximumCellCharacters = 512;
    public const int MaximumEvidenceCharacters = 32_768;

    public ErpReadCapabilityEvidence Validate()
    {
        if (TenantId == Guid.Empty || CompanyId == Guid.Empty || DataSourceId == Guid.Empty)
        {
            throw new ArgumentException(
                "ERP read evidence requires tenant, company and data-source authority.");
        }

        ErpReadCapabilityRequest.RequireCanonicalCapability(
            CapabilityKey,
            nameof(CapabilityKey));
        ErpReadCapabilityRequest.RequireCanonicalVersion(
            CapabilityVersion,
            nameof(CapabilityVersion));

        ArgumentNullException.ThrowIfNull(Columns);
        ArgumentNullException.ThrowIfNull(Rows);

        if (Columns.Count > MaximumColumns)
        {
            throw new InvalidOperationException(
                "ERP read evidence exceeds the global column bound.");
        }

        if (Rows.Count > MaximumRows)
        {
            throw new InvalidOperationException(
                "ERP read evidence exceeds the global row bound.");
        }

        var seenColumns = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in Columns)
        {
            if (string.IsNullOrWhiteSpace(column)
                || !string.Equals(column, column.Trim(), StringComparison.Ordinal)
                || column.Length > 128
                || column.Any(char.IsControl)
                || !seenColumns.Add(column))
            {
                throw new InvalidOperationException(
                    "ERP read evidence contains invalid or duplicate column names.");
            }
        }

        foreach (var row in Rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            row.Validate(Columns.Count);
        }

        if (EvidenceCharacters < 0
            || EvidenceCharacters > MaximumEvidenceCharacters)
        {
            throw new InvalidOperationException(
                "ERP read evidence exceeds the global serialized evidence bound.");
        }

        return this;
    }
}
