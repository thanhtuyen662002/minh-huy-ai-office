using Microsoft.EntityFrameworkCore;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed partial class GroupModelAttemptJournalStore
{
    // Fixed current authority before AND after the existing Gateway wait.
    // Neither unit holds SQL/locks across gate/profile/provider execution.
    // The coordinator must recover an accepted result FIRST; this method
    // independently refuses any already accepted original output.
    internal async Task<GroupModelAttemptResponse> ExecuteAdmittedOriginalAsync(GroupModelAttemptPermit permit,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(permit); token.ThrowIfCancellationRequested();
        var issued = permit.Admission ?? throw Unavailable();
        ValidateEntry(permit.Context);
        var issuedFence = CreateIssuedFence(issued, permit.Context, token);
        await FenceCurrentExecutionAsync();
        AiGatewayResponse response;
        try { response = await admissions!.ExecuteAfterCurrentJournalAsync(permit, token).ConfigureAwait(false); }
        catch (GroupAdmittedExecutionExpiry expiry)
        {
            admissions!.RequireExecutionExpiry(expiry, permit, token);
            ValidateEntry(permit.Context);
            // Fresh owned SQL retirement reauthenticates the actual current
            // claim. It cannot retire a replacement and uses this exact
            // observed timestamp even if UTC rewound during the Gateway wait.
            await new GroupBatchClaimStore(database, worker, clock).RetireObservedExpiryAsync(expiry.Observation, token);
            throw GroupServiceDirectory.Denied();
        }
        await FenceCurrentExecutionAsync();
        return GroupModelAttemptResponse.Parse(permit, response, clock, token);

        async Task FenceCurrentExecutionAsync()
        {
            ValidateEntry(permit.Context);
            await InCurrentUnitAsync(permit.Context, permit.Original, async (now, _, ct) =>
            {
                var (starts, failures) = await ReadJournalAsync(permit.Context, permit.Start.InputSha256, ct);
                if (starts.Length == 0 || starts[^1] != permit.Start || failures.Any(x => x.RequestId == permit.Start.RequestId)
                    || now < permit.Start.StartedAtUtc || now >= permit.Start.DeadlineUtc) throw Unavailable();
                var scope = permit.Context.Scope;
                var accepted = await database.GroupModelAttemptResults.AsNoTracking().AnyAsync(x => x.TenantId == scope.TenantId
                    && x.CompanyId == scope.CompanyId && x.BindingId == scope.SourceBindingId && x.BatchId == permit.Context.BatchId
                    && x.PlanSha256 == issued.Original.Plan.Sha256 && x.OperationId == issued.Original.OperationId, ct);
                if (accepted) throw Unavailable();
                return true;
            }, token, async ct =>
            {
                await RequireAdmittedPermitLockedAsync(permit, ct);
                await new GroupModelAttemptResultPermissionVerifier(database).RequireSafeRuntimeAsync(ct);
            }, issuedFence);
        }
    }
}
