using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

// A fixed category, not a journal write, observed-at timestamp, retry permission,
// permit, proposal or effect capability. Unknown/local/cancellation errors never
// become provider outcomes by inspecting their text, wrappers or inner exceptions.
internal static class GroupModelAttemptProviderFailure
{
    internal static bool TryClassify(Exception? error, out GroupModelAttemptFailure failure,
        CancellationToken token = default)
    {
        failure = default;
        token.ThrowIfCancellationRequested();
        if (error is OperationCanceledException || error is not IAiGatewayProviderFailure provider)
            return false;
        var transient = provider.IsTransient;
        token.ThrowIfCancellationRequested();
        failure = transient ? GroupModelAttemptFailure.TransientProvider : GroupModelAttemptFailure.PermanentProvider;
        return true;
    }
}
