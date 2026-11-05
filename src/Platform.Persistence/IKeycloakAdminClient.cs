namespace MinhHuy.AIOffice.Platform.Persistence;

public interface IKeycloakAdminClient
{
    bool Enabled { get; }
    Task<bool> UserExistsAsync(string email, CancellationToken cancellationToken);
    // False means another request created the exact email concurrently. Never reset it.
    Task<bool> CreateUserAsync(string email, string temporaryPassword, CancellationToken cancellationToken);
    // True while the exact-email account has never chosen its own password (UPDATE_PASSWORD pending).
    Task<bool> PasswordSetupPendingAsync(string email, CancellationToken cancellationToken);
    // Emails the member a Keycloak link to choose their own password (UPDATE_PASSWORD).
    Task SendPasswordSetupEmailAsync(string email, CancellationToken cancellationToken);
}

// Deliberately excludes upstream response bodies, credentials and inner exceptions.
public sealed class KeycloakProvisioningException(bool unavailable = false)
    : Exception("Identity provisioning is temporarily unavailable.")
{
    public bool Unavailable { get; } = unavailable;
}
