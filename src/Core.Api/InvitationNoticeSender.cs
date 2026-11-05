using System.Net;
using System.Net.Mail;
using System.Text;

namespace MinhHuy.AIOffice.Core.Api;

// Tells a member who already has a password that they were added to a company. Keycloak only
// sends account emails (set or reset a password), so this plain notice goes out through the
// same company mailbox, read from the private keycloak-smtp.env mounted as a file secret.
public interface IInvitationNoticeSender
{
    // False when no mailbox is configured; the invitation then simply waits for the next sign-in.
    Task<bool> SendAsync(string email, CancellationToken cancellationToken);
}

public sealed class InvitationMailOptions
{
    public string SettingsFile { get; set; } = "";
    public string AppUrl { get; set; } = "";
}

public sealed class InvitationNoticeException(string detail) : Exception("Invitation notice could not be sent.")
{
    public string Detail { get; } = detail;
}

public sealed record InvitationMailbox(string Host, int Port, bool Tls, string User, string Password, string From, string FromName)
{
    public override string ToString() => $"InvitationMailbox {{ Host = {Host}, Port = {Port} }}";

    // Same keys as Configure-KeycloakEmail.ps1. Null when the file is absent, empty or incomplete.
    public static InvitationMailbox? Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var index = line.IndexOf('=');
            if (index < 1) continue;
            var value = line[(index + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'') value = value[1..^1];
            values[line[..index].Trim()] = value;
        }
        string Value(string key) => values.TryGetValue(key, out var value) ? value : "";
        if (Value("SMTP_HOST").Length == 0 || Value("SMTP_USER").Length == 0 || Value("SMTP_PASSWORD").Length == 0) return null;
        var port = int.TryParse(Value("SMTP_PORT"), out var parsed) && parsed is > 0 and < 65536 ? parsed : 587;
        var security = Value("SMTP_SECURITY").ToLowerInvariant();
        var from = Value("SMTP_FROM").Length != 0 ? Value("SMTP_FROM") : Value("SMTP_USER");
        var name = Value("SMTP_FROM_NAME").Length != 0 ? Value("SMTP_FROM_NAME") : "AI Office";
        return new(Value("SMTP_HOST"), port, security != "none", Value("SMTP_USER"), Value("SMTP_PASSWORD"), from, name);
    }
}

public sealed class SmtpInvitationNoticeSender(InvitationMailOptions options) : IInvitationNoticeSender
{
    public async Task<bool> SendAsync(string email, CancellationToken cancellationToken)
    {
        if (options.SettingsFile.Length == 0 || !File.Exists(options.SettingsFile)) return false;
        var mailbox = InvitationMailbox.Parse(await File.ReadAllTextAsync(options.SettingsFile, cancellationToken));
        if (mailbox is null) return false;
        // System.Net.Mail upgrades with STARTTLS (port 587); implicit TLS on 465 is not supported.
        if (mailbox.Port == 465) throw new InvitationNoticeException("port 465 (implicit TLS) is not supported; use 587");
        try
        {
            using var message = new MailMessage(new MailAddress(mailbox.From, mailbox.FromName, Encoding.UTF8), new MailAddress(email))
            {
                Subject = "Bạn đã được thêm vào AI Office",
                SubjectEncoding = Encoding.UTF8,
                BodyEncoding = Encoding.UTF8,
                Body = Body(options.AppUrl),
            };
            using var client = new SmtpClient(mailbox.Host, mailbox.Port)
            {
                EnableSsl = mailbox.Tls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(mailbox.User, mailbox.Password),
                Timeout = 15_000,
            };
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (SmtpException error) { throw new InvitationNoticeException($"SMTP {error.StatusCode}"); }
        catch (Exception error) when (error is IOException or InvalidOperationException or FormatException or ArgumentException or
            System.Security.Authentication.AuthenticationException)
        { throw new InvitationNoticeException(error.GetType().Name); }
        return true;
    }

    public static string Body(string appUrl)
    {
        var link = Uri.TryCreate(appUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.GetLeftPart(UriPartial.Authority) : "";
        return "Xin chào,\n\n"
            + "Bạn vừa được thêm vào một công ty trên AI Office.\n"
            + (link.Length != 0 ? $"Mở {link} và đăng nhập bằng tài khoản hiện có của bạn.\n" : "Hãy đăng nhập AI Office bằng tài khoản hiện có của bạn.\n")
            + "Nếu quên mật khẩu, bấm \"Quên mật khẩu?\" ở trang đăng nhập.\n\n"
            + "AI Office";
    }
}

internal static class InvitationNoticeRegistration
{
    public static void AddInvitationNotices(this WebApplicationBuilder builder)
    {
        var options = new InvitationMailOptions();
        builder.Configuration.GetSection("AIOffice:InvitationMail").Bind(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IInvitationNoticeSender, SmtpInvitationNoticeSender>();
    }
}
