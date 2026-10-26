namespace MinhHuy.AIOffice.Platform.Persistence;

public interface IKeycloakAdminClient
{
    bool Enabled { get; }
    Task<bool> UserExistsAsync(string email, CancellationToken cancellationToken);
    // False means another request created the exact email concurrently. Never reset it.
    Task<bool> CreateUserAsync(string email, string temporaryPassword, CancellationToken cancellationToken);
}

// Deliberately excludes upstream response bodies, credentials and inner exceptions.
public sealed class KeycloakProvisioningException(bool unavailable = false)
    : Exception("Identity provisioning is temporarily unavailable.")
{
    public bool Unavailable { get; } = unavailable;
}
