namespace MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

public sealed record GroupListenerAccountScope(Guid TenantId, Guid CompanyId, Guid ConnectorAccountId)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || CompanyId == Guid.Empty || ConnectorAccountId == Guid.Empty)
            throw new InvalidOperationException("Listener account scope is incomplete.");
    }
}

public enum GroupListenerOperation { Acquire = 1, Renew = 2, Stop = 3 }

public sealed record GroupListenerCommand(Guid OwnerId, GroupListenerOperation Operation, long ExpectedEpoch)
{
    public void Validate()
    {
        if (OwnerId == Guid.Empty || !Enum.IsDefined(Operation) ||
            (Operation == GroupListenerOperation.Acquire ? ExpectedEpoch != 0 : ExpectedEpoch <= 0))
            throw new InvalidOperationException("Listener command is invalid.");
    }
}

public sealed record GroupListenerLeaseSnapshot(GroupListenerAccountScope Account, Guid OwnerId, long Epoch,
    DateTimeOffset HeartbeatAtUtc, DateTimeOffset ExpiresAtUtc);

public sealed record GroupListenerTransition(GroupListenerLeaseSnapshot Lease, bool Changed,
    string? CoverageReason, DateTimeOffset? CoverageOpenedAtUtc);

// This policy does not authenticate a service or acquire a lock. The SQL caller
// must resolve current Ingest authority and serialize one account through commit.
// OwnerId identifies one listener process; restart uses a new owner, never a
// shared installation identity. Retain the row so an expired epoch never resets.
public static class GroupListenerLeasePolicy
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    public static GroupListenerTransition Apply(GroupListenerAccountScope account, GroupListenerCommand command,
        GroupListenerLeaseSnapshot? current, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(command);
        account.Validate(); command.Validate();
        if (nowUtc.Offset != TimeSpan.Zero || nowUtc > DateTimeOffset.MaxValue - LeaseDuration)
            throw Unavailable();
        if (current is not null && (current.Account != account || current.OwnerId == Guid.Empty || current.Epoch <= 0 ||
            current.HeartbeatAtUtc.Offset != TimeSpan.Zero || current.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            current.HeartbeatAtUtc > nowUtc || current.ExpiresAtUtc < current.HeartbeatAtUtc ||
            current.ExpiresAtUtc - current.HeartbeatAtUtc > LeaseDuration)) throw Unavailable();

        if (command.Operation == GroupListenerOperation.Acquire)
        {
            if (current is not null && current.ExpiresAtUtc > nowUtc)
            {
                if (current.OwnerId != command.OwnerId) throw Denied();
                // Lost acquire ACK can be reconciled without renewing the lease.
                return new(current, false, null, null);
            }
            if (current?.Epoch == long.MaxValue) throw Unavailable();
            var epoch = current is null ? 1 : current.Epoch + 1;
            return new(new(account, command.OwnerId, epoch, nowUtc, nowUtc + LeaseDuration), true,
                current is null ? "listener-started" : "listener-expired", current?.HeartbeatAtUtc ?? nowUtc);
        }

        if (current is null || current.OwnerId != command.OwnerId || current.Epoch != command.ExpectedEpoch) throw Denied();
        if (command.Operation == GroupListenerOperation.Stop && current.ExpiresAtUtc == current.HeartbeatAtUtc)
            return new(current, false, null, null);
        if (current.ExpiresAtUtc <= nowUtc) throw Denied();
        return command.Operation == GroupListenerOperation.Renew
            ? new(new(account, command.OwnerId, current.Epoch, nowUtc, nowUtc + LeaseDuration), true, null, null)
            : new(new(account, command.OwnerId, current.Epoch, nowUtc, nowUtc), true, "listener-stopped", nowUtc);
    }

    private static UnauthorizedAccessException Denied() => new("Listener ownership is not available.");
    private static InvalidOperationException Unavailable() => new("Listener lease is not available.");
}
