using MinhHuy.AIOffice.Core.Api;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class InvitationNoticeSenderTests
{
    [Fact]
    public void Mailbox_reads_the_keycloak_mail_settings_file()
    {
        var mailbox = InvitationMailbox.Parse("# private\r\nSMTP_HOST=smtp.example.test\r\nSMTP_PORT=587\r\nSMTP_USER=office@example.test\r\n" +
            "SMTP_PASSWORD=\"synthetic app password\"\r\nSMTP_FROM_NAME=AI Office Minh Huy\r\n");
        Assert.NotNull(mailbox);
        Assert.Equal(("smtp.example.test", 587, true, "office@example.test", "synthetic app password", "office@example.test", "AI Office Minh Huy"),
            (mailbox.Host, mailbox.Port, mailbox.Tls, mailbox.User, mailbox.Password, mailbox.From, mailbox.FromName));
        Assert.DoesNotContain("synthetic app password", mailbox.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("SMTP_HOST=smtp.example.test\nSMTP_USER=office@example.test")]
    [InlineData("SMTP_USER=office@example.test\nSMTP_PASSWORD=x")]
    public void Missing_or_incomplete_settings_mean_no_mailbox(string text) => Assert.Null(InvitationMailbox.Parse(text));

    [Fact]
    public async Task No_settings_file_sends_nothing()
    {
        Assert.False(await new SmtpInvitationNoticeSender(new InvitationMailOptions()).SendAsync("member@example.com", default));
        var empty = Path.GetTempFileName();
        try { Assert.False(await new SmtpInvitationNoticeSender(new InvitationMailOptions { SettingsFile = empty }).SendAsync("member@example.com", default)); }
        finally { File.Delete(empty); }
    }

    [Fact]
    public void Notice_links_only_an_https_app_origin()
    {
        Assert.Contains("Mở https://ai.example.test và đăng nhập", SmtpInvitationNoticeSender.Body("https://ai.example.test/some/path"));
        Assert.DoesNotContain("http://", SmtpInvitationNoticeSender.Body("http://ai.example.test"));
        Assert.Contains("Quên mật khẩu?", SmtpInvitationNoticeSender.Body(""));
    }
}
