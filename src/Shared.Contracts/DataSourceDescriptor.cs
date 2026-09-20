namespace MinhHuy.AIOffice.Shared.Contracts;

public sealed record DataSourceDescriptor
{
    private DataSourceDescriptor(
        Guid tenantId,
        Guid companyId,
        Guid id,
        string logicalName,
        string kind,
        string environment,
        string purpose,
        bool allowRead,
        bool allowWrite,
        int maxConcurrency,
        bool isEnabled)
    {
        TenantId = tenantId;
        CompanyId = companyId;
        Id = id;
        LogicalName = logicalName;
        Kind = kind;
        Environment = environment;
        Purpose = purpose;
        AllowRead = allowRead;
        AllowWrite = allowWrite;
        MaxConcurrency = maxConcurrency;
        IsEnabled = isEnabled;
    }

    public Guid TenantId { get; }

    public Guid CompanyId { get; }

    public Guid Id { get; }

    public string LogicalName { get; }

    public string Kind { get; }

    public string Environment { get; }

    public string Purpose { get; }

    public bool AllowRead { get; }

    public bool AllowWrite { get; }

    public int MaxConcurrency { get; }

    public bool IsEnabled { get; }

    public static DataSourceDescriptor Create(
        Guid tenantId,
        Guid companyId,
        Guid id,
        string logicalName,
        string kind,
        string environment,
        string purpose,
        bool allowRead,
        bool allowWrite,
        int maxConcurrency,
        bool isEnabled = true)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId must be non-empty.", nameof(tenantId));
        }

        if (companyId == Guid.Empty)
        {
            throw new ArgumentException("CompanyId must be non-empty.", nameof(companyId));
        }

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must be non-empty.", nameof(id));
        }

        logicalName = RequireText(logicalName, 200, nameof(logicalName));
        kind = RequireText(kind, 100, nameof(kind));
        environment = RequireText(environment, 50, nameof(environment));
        purpose = RequireText(purpose, 200, nameof(purpose));

        if (!allowRead && !allowWrite)
        {
            throw new ArgumentException(
                "At least one data-source access mode must be allowed.",
                nameof(allowRead));
        }

        if (maxConcurrency is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrency),
                "MaxConcurrency must be between 1 and 1024.");
        }

        return new DataSourceDescriptor(
            tenantId,
            companyId,
            id,
            logicalName,
            kind,
            environment,
            purpose,
            allowRead,
            allowWrite,
            maxConcurrency,
            isEnabled);
    }

    private static string RequireText(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        var normalized = value.Trim();

        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"Value must be at most {maximumLength} characters.",
                parameterName);
        }

        return normalized;
    }
}
