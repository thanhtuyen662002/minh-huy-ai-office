extern alias RuntimeWorker;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;

namespace MinhHuy.AIOffice.GroupIntake.RuntimeProof;

internal static class OwnedRecoveryHostProof
{
    internal static async Task RunAsync(string mode, string root, ProofConfiguration config, GroupExternalIdentity external,
        OwnedProofSecrets countedSecrets, CompositeSecretResolver secrets)
    {
        var settings = new Dictionary<string, string?>
        {
            ["AIOffice:GroupIntake:Enabled"] = "true",
            ["AIOffice:GroupIntake:ConnectorRecoveryEnabled"] = "true",
            ["AIOffice:GroupIntake:OwnedSyntheticFixture"] = "true",
            ["AIOffice:GroupIntake:OwnedDisposableFixture"] = "true",
            ["AIOffice:GroupIntake:Connector:TenantId"] = config.TenantId.ToString("D"),
            ["AIOffice:GroupIntake:Connector:CompanyId"] = config.CompanyId.ToString("D"),
            ["AIOffice:GroupIntake:Connector:ConnectorAccountId"] = config.AccountId.ToString("D"),
            ["AIOffice:GroupIntake:Connector:ServiceId"] = config.ServiceId.ToString("D"),
            ["AIOffice:GroupIntake:Connector:CredentialEpoch"] = "1",
            ["AIOffice:GroupIntake:Connector:BackendOrigin"] = config.Origin,
            ["AIOffice:GroupIntake:Connector:PrivateRoot"] = root,
            ["AIOffice:GroupIntake:Connector:SigningSecretRef"] = "secretref://env/AIOFFICE_GROUP_PROOF_SIGNING_KEY",
            ["AIOffice:GroupIntake:Connector:SpoolSecretRef"] = "secretref://env/AIOFFICE_GROUP_PROOF_SPOOL_KEY",
            ["AIOffice:GroupIntake:Connector:SpoolKeyId"] = "spool-v1",
            ["AIOffice:GroupIntake:Connector:Provider"] = external.Provider,
            ["AIOffice:GroupIntake:Connector:ExternalAccountId"] = external.AccountId,
            ["AIOffice:GroupIntake:Connector:Sources:0:SourceBindingId"] = config.SourceId.ToString("D"),
            ["AIOffice:GroupIntake:Connector:Sources:0:ExternalGroupId"] = external.GroupId
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(secrets);
        // Match the owned container's runtime principal. DB access occurs in
        // Core here; separate native recovery-startup exercises SQL permission
        // execution inside its network. Do not claim this host opened SQL.
        var connection = Environment.GetEnvironmentVariable("AIOFFICE_GROUP_PROOF_RUNTIME_CONNECTION");
        var sql = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection);
        if (sql.DataSource != "sql" || sql.InitialCatalog != "AIOfficeLocal" || sql.UserID != "aioffice_runtime" ||
            sql.IntegratedSecurity || sql.Authentication != Microsoft.Data.SqlClient.SqlAuthenticationMethod.NotSpecified || !sql.Enlist ||
            sql.AttachDBFilename.Length != 0 || sql.FailoverPartner.Length != 0 || sql.Password.Length is < 32 or > 128 ||
            sql.Password.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('_' or '-'))) throw new InvalidOperationException();
        services.AddDbContext<PlatformDbContext>(options => options.UseSqlServer(sql.ConnectionString));
        if (!services.AddGroupConnectorRecovery(configuration, "Development", true)) throw new InvalidOperationException();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<GroupIngressPermissionVerifier>();
        var runtime = provider.GetRequiredService<GroupConnectorRecoveryRuntime>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        if (mode == "host-capture-send")
        {
            const string text = "owned managed recovery 😀\uFEFF ";
            var payload = new GroupIngressPayload(new(external, "spool-message-" + config.EventId, "spool-event-" + config.EventId,
                "owned-sender", null, GroupSourceEventKind.NewText, config.OccurredAtUtc, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), false),
                text, true, false, false, config.OwnerId, 1);
            _ = await runtime.CaptureAsync(payload, deadline.Token);
            throw new InvalidOperationException(); // Parent must kill only after observed commit, before ACK.
        }
        var pending = Directory.GetFiles(root, "*.spool", SearchOption.AllDirectories).Single();
        var retained = File.ReadAllBytes(pending);
        if (mode == "host-deny")
        {
            var pass = await runtime.RecoverOnceAsync(deadline.Token);
            if (pass.Refused != 1 || pass.Remaining != 1 || pass.Committed != 0 || countedSecrets.SpoolKeyCalls != 0 || !File.ReadAllBytes(pending).SequenceEqual(retained))
                throw new InvalidOperationException();
            Console.WriteLine("PASS owned managed recovery current denial before key retains ciphertext");
            return;
        }
        if (mode != "host-recover") throw new InvalidOperationException();
        var hosted = provider.GetServices<IHostedService>().Single() as GroupConnectorRecoveryHostedService ?? throw new InvalidOperationException();
        try
        {
            await hosted.StartAsync(deadline.Token);
            while (File.Exists(pending)) await Task.Delay(50, deadline.Token);
            if (Directory.GetFiles(root, "*.spool", SearchOption.AllDirectories).Length != 0 || countedSecrets.SpoolKeyCalls == 0)
                throw new InvalidOperationException();
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await hosted.StopAsync(stop.Token);
        }
        Console.WriteLine("PASS owned managed hosted recovery deletes only actual acknowledged capture");
    }
}
