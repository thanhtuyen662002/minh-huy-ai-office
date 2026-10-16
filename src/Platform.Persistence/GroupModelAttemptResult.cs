using System.Security.Cryptography;
using System.Text;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Closed recovery bytes only. A decoded result is neither an authenticated SQL
// row nor current authority, provider qualification, exhaustion or an effect.
// The later SQL store must authenticate the original plan and journal row under
// current locks before accepting these bytes and fence again before effects.
internal sealed class GroupModelAttemptResult
{
    internal const int MaximumBytes = 66000;
    private static readonly byte[] Magic = "AIOGRES1"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly byte[] output;
    private readonly string planHash;
    private readonly string inputHash;
    private readonly string attemptHash;
    private readonly int chunkOrdinal;
    private readonly Guid operation;

    private GroupModelAttemptResult(string planHash, int chunkOrdinal, Guid operation, string inputHash,
        string attemptHash, GroupModelAttemptObservedOutput observed, byte[] output, GroupGroundedWorkProposal proposal)
    {
        this.planHash = planHash; this.chunkOrdinal = chunkOrdinal; this.operation = operation;
        this.inputHash = inputHash; this.attemptHash = attemptHash;
        Observed = observed; this.output = output.ToArray(); Proposal = proposal;
    }
    internal GroupModelAttemptObservedOutput Observed { get; }
    internal GroupGroundedWorkProposal Proposal { get; }
    public override string ToString() => "Group original attempt result (private structural recovery).";

    internal static GroupModelAttemptResult Create(GroupModelAttemptResponse response,
        GroupAutomaticInputPlan.RestoredChunk restored, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(response); ArgumentNullException.ThrowIfNull(restored); token.ThrowIfCancellationRequested();
        try
        {
            var original = response.Permit.Original ?? throw Unavailable();
            var binding = GroupModelAttemptOriginalBinding.Create(original.Plan, restored, token);
            var context = response.Permit.Context;
            if (response.Disposition != GroupModelAttemptResponseDisposition.Parsed || response.Proposal is null
                || response.Observed is null || !ReferenceEquals(context, binding.Context)
                || !ReferenceEquals(original.Context, context) || !ReferenceEquals(response.Proposal.Preparation, context.Preparation)
                || original.OperationId != binding.OperationId
                || !CryptographicOperations.FixedTimeEquals(original.Plan.Encode(), binding.Plan.Encode())) throw Unavailable();
            RequireStart(response.Permit.Start, binding);
            RequireObserved(response.Observed, response.Permit.Start);
            var bytes = response.Proposal.EncodeValidatedOutput();
            if (bytes.Length is < 1 or > GroupGroundedWorkProposal.MaximumOutputUtf8Bytes) throw Unavailable();
            token.ThrowIfCancellationRequested();
            return new(binding.Plan.Sha256, restored.Original.Ordinal, binding.OperationId,
                restored.Original.InputSha256, StartHash(response.Permit.Start), response.Observed, bytes, response.Proposal);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { throw Unavailable(); }
    }

    internal byte[] Encode()
    {
        using var stream = new MemoryStream(MaximumBytes);
        try
        {
            using var writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(Magic); writer.Write(SchemaHash());
            writer.Write(Convert.FromHexString(planHash)); writer.Write((byte)chunkOrdinal);
            writer.Write(operation.ToByteArray()); writer.Write(Convert.FromHexString(inputHash));
            writer.Write(Convert.FromHexString(attemptHash)); Text(Observed.ProviderId); Text(Observed.Model);
            writer.Write(Observed.InputTokens); writer.Write(Observed.OutputTokens); writer.Write(Observed.ObservedAtUtc.UtcTicks);
            writer.Write(output.Length); writer.Write(output); writer.Flush();
            if (stream.Length > MaximumBytes) throw Unavailable();
            return stream.ToArray();

            void Text(string value)
            { var bytes = Utf8.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        }
        finally { CryptographicOperations.ZeroMemory(stream.GetBuffer()); }
    }

    internal static GroupModelAttemptResult Read(byte[]? value, GroupAutomaticInputPlan plan,
        GroupAutomaticInputPlan.RestoredChunk restored, GroupModelAttemptStarted expectedStart,
        DateTimeOffset now, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(restored);
        ArgumentNullException.ThrowIfNull(expectedStart); token.ThrowIfCancellationRequested();
        if (value is null || value.Length is < 200 or > MaximumBytes) throw Unavailable();
        try
        {
            var binding = GroupModelAttemptOriginalBinding.Create(plan, restored, token);
            RequireStart(expectedStart, binding);
            using var stream = new MemoryStream(value.ToArray(), false);
            using var reader = new BinaryReader(stream, Utf8, true);
            if (!Bytes(Magic.Length).AsSpan().SequenceEqual(Magic) || !Bytes(32).AsSpan().SequenceEqual(SchemaHash())) throw Unavailable();
            var storedPlan = Convert.ToHexString(Bytes(32)); var ordinal = reader.ReadByte();
            var operation = new Guid(Bytes(16)); var input = Convert.ToHexString(Bytes(32)); var attempt = Convert.ToHexString(Bytes(32));
            if (storedPlan != binding.Plan.Sha256 || ordinal != restored.Original.Ordinal || operation != binding.OperationId
                || input != restored.Original.InputSha256 || attempt != StartHash(expectedStart)) throw Unavailable();
            var observed = new GroupModelAttemptObservedOutput(Text(128), Text(256), reader.ReadInt64(), reader.ReadInt64(),
                new(reader.ReadInt64(), TimeSpan.Zero));
            RequireObserved(observed, expectedStart);
            if (now.Offset != TimeSpan.Zero || observed.ObservedAtUtc > now) throw Unavailable();
            var length = reader.ReadInt32();
            if (length is < 1 or > GroupGroundedWorkProposal.MaximumOutputUtf8Bytes) throw Unavailable();
            var output = Bytes(length);
            if (stream.Position != stream.Length) throw Unavailable();
            // Recovery reparses the full unchanged output with the original
            // chunk rebuilt from fresh source and history reader results.
            var proposal = GroupGroundedWorkProposal.Parse(binding.Context.Preparation, Utf8.GetString(output));
            token.ThrowIfCancellationRequested();
            var result = new GroupModelAttemptResult(storedPlan, ordinal, operation, input, attempt, observed, output, proposal);
            if (!CryptographicOperations.FixedTimeEquals(result.Encode(), value)) throw Unavailable();
            return result;

            byte[] Bytes(int count)
            {
                if (count > stream.Length - stream.Position) throw Unavailable();
                var bytes = reader.ReadBytes(count); if (bytes.Length != count) throw Unavailable(); return bytes;
            }
            string Text(int maximum)
            { var count = reader.ReadUInt16(); if (count > maximum) throw Unavailable(); return Utf8.GetString(Bytes(count)); }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or EndOfStreamException or OverflowException)
        { throw Unavailable(); }
    }

    private static void RequireStart(GroupModelAttemptStarted start, GroupModelAttemptOriginalBinding binding)
    {
        if (start.Scope != binding.Context.Scope || start.BatchId != binding.Context.BatchId
            || start.InputSha256 != GroupModelAttemptBudget.InputIdentity(binding.Context)
            || start.Ordinal is < 1 or > GroupModelAttemptBudget.MaximumAttempts || start.RequestId == Guid.Empty
            || (start.Ordinal == 1 ? start.PredecessorRequestId is not null
                : start.PredecessorRequestId is null || start.PredecessorRequestId == Guid.Empty || start.PredecessorRequestId == start.RequestId)
            || start.ClaimOperationId == Guid.Empty || start.ClaimEpoch <= 0
            || start.StartedAtUtc.Offset != TimeSpan.Zero || start.DeadlineUtc.Offset != TimeSpan.Zero
            || start.DeadlineUtc <= start.StartedAtUtc
            || start.DeadlineUtc - start.StartedAtUtc > GroupModelAttemptBudget.MaximumAttemptLifetime) throw Unavailable();
    }
    private static void RequireObserved(GroupModelAttemptObservedOutput observed, GroupModelAttemptStarted start)
    {
        if (!Canonical(observed.ProviderId, 128) || !Canonical(observed.Model, 256)
            || observed.InputTokens < 0 || observed.OutputTokens is < 0 or > 32768
            || observed.ObservedAtUtc.Offset != TimeSpan.Zero || observed.ObservedAtUtc < start.StartedAtUtc
            || observed.ObservedAtUtc >= start.DeadlineUtc) throw Unavailable();
    }
    private static bool Canonical(string? value, int maximum) => value is not null && !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && value.Length <= maximum && !value.Any(char.IsControl) && Utf8.GetByteCount(value) <= maximum;

    private static string StartHash(GroupModelAttemptStarted start)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(GroupModelAttemptBudget.Version);
        writer.Write(start.Scope.TenantId.ToByteArray()); writer.Write(start.Scope.CompanyId.ToByteArray()); writer.Write(start.Scope.SourceBindingId.ToByteArray());
        writer.Write(start.BatchId.ToByteArray()); writer.Write(Convert.FromHexString(start.InputSha256)); writer.Write(start.Ordinal);
        writer.Write(start.RequestId.ToByteArray()); writer.Write((start.PredecessorRequestId ?? Guid.Empty).ToByteArray());
        writer.Write(start.ClaimOperationId.ToByteArray()); writer.Write(start.ClaimEpoch);
        writer.Write(start.StartedAtUtc.UtcTicks); writer.Write(start.DeadlineUtc.UtcTicks); writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static byte[] SchemaHash() => SHA256.HashData(Utf8.GetBytes(GroupGroundedWorkProposal.FormatVersion + "\n"
        + GroupSecretQuarantine.PolicyVersion + "\n" + GroupModelAttemptBudget.Version + "\n" + GroupGroundedWorkProposal.ResponseSchema));
    private static InvalidOperationException Unavailable() => new("Group original attempt result is not available.");
}
