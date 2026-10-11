using System.Text;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal enum GroupModelAttemptResponseDisposition { Parsed = 1, InvalidOutput = 2, Expired = 3 }

// Structural response only. This class neither invokes a provider nor grants
// SQL effects, exhaustion or current authority after a provider wait. The
// consumer must recheck source/brain/claim and its qualified Gateway profile;
// the atomic effect store still owns its final locked dependency checks.
internal sealed class GroupModelAttemptResponse
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private GroupModelAttemptResponse(GroupModelAttemptPermit permit, GroupModelAttemptResponseDisposition disposition,
        GroupGroundedWorkProposal? proposal)
    { Permit = permit; Disposition = disposition; Proposal = proposal; }
    internal GroupModelAttemptPermit Permit { get; }
    internal GroupModelAttemptResponseDisposition Disposition { get; }
    internal GroupGroundedWorkProposal? Proposal { get; }
    public override string ToString() => "Group model attempt response (private structural result).";

    internal static GroupModelAttemptResponse Parse(GroupModelAttemptPermit permit, AiGatewayResponse? response,
        TimeProvider clock, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(permit); ArgumentNullException.ThrowIfNull(clock); token.ThrowIfCancellationRequested();
        var context = permit.Context; var start = permit.Start;
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(start);
        var receipt = context.Preparation.Context.Handle.Receipt;
        if (start.Scope != context.Scope || start.BatchId != context.BatchId
            || start.InputSha256 != GroupModelAttemptBudget.InputIdentity(context)
            || start.Ordinal is < 1 or > GroupModelAttemptBudget.MaximumAttempts || start.RequestId == Guid.Empty
            || (start.Ordinal == 1 ? start.PredecessorRequestId is not null
                : start.PredecessorRequestId is null || start.PredecessorRequestId == Guid.Empty || start.PredecessorRequestId == start.RequestId)
            || start.ClaimOperationId != receipt.OperationId || start.ClaimEpoch != receipt.Epoch
            || start.StartedAtUtc.Offset != TimeSpan.Zero || start.DeadlineUtc.Offset != TimeSpan.Zero
            || start.StartedAtUtc < receipt.IssuedAtUtc || start.DeadlineUtc > receipt.ExpiresAtUtc
            || start.DeadlineUtc <= start.StartedAtUtc
            || start.DeadlineUtc - start.StartedAtUtc > GroupModelAttemptBudget.MaximumAttemptLifetime) throw Unavailable();
        var began = UtcNow();
        if (began < start.StartedAtUtc) throw Unavailable();
        if (began >= start.DeadlineUtc) return Result(GroupModelAttemptResponseDisposition.Expired);
        var request = context.CreateGatewayRequest(start.RequestId);
        GroupGroundedWorkProposal? proposal = null;
        try
        {
            if (response is not null)
            {
                response.ValidateFor(request);
                if (!Canonical(response.Model, 256) || !Canonical(response.ProviderId, 128)
                    || response.OutputTokens > 32768) throw Unavailable();
                // The existing closed parser enforces the full current source
                // disposition set, literal evidence, Unicode and quarantine.
                // It does not qualify semantics, tokenizer or actual routing.
                proposal = GroupGroundedWorkProposal.Parse(context.Preparation, response.Output);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { proposal = null; }
        token.ThrowIfCancellationRequested();
        var completed = UtcNow();
        if (completed < began) throw Unavailable();
        if (completed >= start.DeadlineUtc) return Result(GroupModelAttemptResponseDisposition.Expired);
        return proposal is null ? Result(GroupModelAttemptResponseDisposition.InvalidOutput)
            : new(permit, GroupModelAttemptResponseDisposition.Parsed, proposal);

        GroupModelAttemptResponse Result(GroupModelAttemptResponseDisposition disposition) => new(permit, disposition, null);
        DateTimeOffset UtcNow()
        { var now = clock.GetUtcNow(); if (now.Offset != TimeSpan.Zero) throw Unavailable(); return now; }
    }

    private static bool Canonical(string? value, int limit) => value is not null && !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && value.Length <= limit && !value.Any(char.IsControl) && StrictUtf8.GetByteCount(value) <= limit;
    private static InvalidOperationException Unavailable() => new("Group model attempt response is not available.");
}
