namespace MinhHuy.AIOffice.Platform.Configuration;

public sealed record SecretReference
{
    public const string Scheme = "secretref";
    public const int MaximumLength = 512;

    private SecretReference(string value, string provider, string resource)
    {
        Value = value;
        Provider = provider;
        Resource = resource;
    }

    public string Value { get; }

    public string Provider { get; }

    public string Resource { get; }

    public static SecretReference Parse(string? value)
    {
        if (!TryParse(value, out var reference))
        {
            throw new FormatException(
                "Secret references must use the form secretref://provider/resource and must not contain credentials, query strings, or fragments.");
        }

        return reference;
    }

    public static bool TryParse(string? value, out SecretReference? reference)
    {
        reference = null;

        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumLength ||
            value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var provider = uri.Host;
        if (!IsValidProvider(provider))
        {
            return false;
        }

        var resource = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (string.IsNullOrWhiteSpace(resource) ||
            resource.Any(char.IsWhiteSpace) ||
            resource.Any(char.IsControl))
        {
            return false;
        }

        var canonicalProvider = provider.ToLowerInvariant();
        var canonicalResource = string.Join(
            '/',
            resource.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

        if (string.IsNullOrEmpty(canonicalResource))
        {
            return false;
        }

        reference = new SecretReference(
            $"{Scheme}://{canonicalProvider}/{canonicalResource}",
            canonicalProvider,
            resource);

        return true;
    }

    public override string ToString() => Value;

    private static bool IsValidProvider(string provider)
    {
        if (provider.Length is < 1 or > 64 ||
            !char.IsAsciiLetterOrDigit(provider[0]) ||
            !char.IsAsciiLetterOrDigit(provider[^1]))
        {
            return false;
        }

        return provider.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '.');
    }
}
