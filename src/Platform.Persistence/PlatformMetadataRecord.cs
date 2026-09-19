namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class PlatformMetadataRecord
{
    public required string Key { get; set; }

    public string? Value { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
