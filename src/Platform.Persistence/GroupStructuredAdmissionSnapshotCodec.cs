using System.Security.Cryptography;
using System.Text;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Closed historical metadata only. Neither checksum-valid bytes nor a decoded
// public snapshot issues original-plan admission or a provider-release permit.
internal static class GroupStructuredAdmissionSnapshotCodec
{
    internal const int MaximumBytes = 10000;
    private static readonly byte[] Magic = "AIOGADM1"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Write(AiStructuredAdmissionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic); Hash(snapshot.GatewaySha256); writer.Write(snapshot.WholeOperationTimeoutTicks);
        Hash(snapshot.InputSha256); writer.Write(snapshot.InputBytes); Hash(snapshot.SchemaSha256); writer.Write(snapshot.SchemaBytes);
        Text(snapshot.TokenizerVersion, 128); writer.Write(snapshot.InputTokens); writer.Write(snapshot.SchemaTokens);
        writer.Write(checked((byte)snapshot.Routes.Count));
        foreach (var route in snapshot.Routes)
        {
            writer.Write(checked((byte)route.Ordinal)); Text(route.ProviderId, 128); Text(route.ConfiguredModel, 256);
            Hash(route.EndpointSha256); Hash(route.CandidateSha256); Hash(route.EvidenceSha256);
            writer.Write(route.ContextWindowTokens); writer.Write(route.FramingReserveTokens);
            writer.Write(route.MaxOutputTokens); writer.Write(route.MaximumInputTokens); writer.Write(checked((byte)route.EchoedModels.Count));
            foreach (var model in route.EchoedModels) Text(model, 256);
        }
        writer.Flush();
        if (stream.Length + 32 > MaximumBytes) throw Unavailable();
        var body = stream.ToArray(); var output = new byte[body.Length + 32]; body.CopyTo(output, 0);
        SHA256.HashData(body).CopyTo(output, body.Length); return output;

        void Hash(string value) => writer.Write(Convert.FromHexString(value));
        void Text(string value, int limit)
        {
            var bytes = Utf8.GetBytes(value);
            if (bytes.Length < 1 || bytes.Length > limit) throw Unavailable();
            writer.Write(checked((ushort)bytes.Length)); writer.Write(bytes);
        }
    }

    internal static AiStructuredAdmissionSnapshot Read(byte[] encoded)
    {
        if (encoded is null || encoded.Length < 256 || encoded.Length > MaximumBytes) throw Unavailable();
        // Freeze bounded caller bytes before checksum verification and parsing.
        var frozen = encoded.ToArray(); var bodyLength = frozen.Length - 32;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(frozen.AsSpan(0, bodyLength)), frozen.AsSpan(bodyLength))) throw Unavailable();
        try
        {
            using var stream = new MemoryStream(frozen, 0, bodyLength, false);
            using var reader = new BinaryReader(stream, Utf8);
            if (!Bytes(Magic.Length).SequenceEqual(Magic)) throw Unavailable();
            var gateway = Hash(); var timeout = reader.ReadInt64();
            var inputHash = Hash(); var inputBytes = reader.ReadInt32(); var schemaHash = Hash(); var schemaBytes = reader.ReadInt32();
            var tokenizer = Text(128); var inputTokens = reader.ReadInt32(); var schemaTokens = reader.ReadInt32();
            var routeCount = reader.ReadByte(); if (routeCount is < 1 or > 2) throw Unavailable();
            var routes = new AiStructuredAdmissionRoute[routeCount];
            for (var index = 0; index < routeCount; index++)
            {
                var ordinal = reader.ReadByte(); var provider = Text(128); var model = Text(256);
                var endpoint = Hash(); var candidate = Hash(); var evidence = Hash();
                var window = reader.ReadInt64(); var framing = reader.ReadInt32(); var output = reader.ReadInt32(); var maximumInput = reader.ReadInt64();
                var aliasCount = reader.ReadByte(); if (aliasCount is < 1 or > 16) throw Unavailable();
                var aliases = new string[aliasCount];
                for (var alias = 0; alias < aliasCount; alias++) aliases[alias] = Text(256);
                routes[index] = new(ordinal, provider, model, endpoint, candidate, evidence, window, framing, output, maximumInput, aliases);
            }
            if (stream.Position != bodyLength) throw Unavailable();
            var snapshot = new AiStructuredAdmissionSnapshot(gateway, timeout, inputHash, inputBytes, schemaHash, schemaBytes,
                tokenizer, inputTokens, schemaTokens, routes);
            // One encoding: reject alternate alias ordering or any noncanonical
            // representation even when the untrusted sender recomputes SHA256.
            if (!CryptographicOperations.FixedTimeEquals(Write(snapshot), frozen)) throw Unavailable();
            return snapshot;

            byte[] Bytes(int count)
            {
                if (count < 0 || count > stream.Length - stream.Position) throw Unavailable();
                var value = reader.ReadBytes(count); if (value.Length != count) throw Unavailable(); return value;
            }
            string Hash() => Convert.ToHexString(Bytes(32));
            string Text(int limit)
            {
                var count = reader.ReadUInt16(); if (count < 1 || count > limit) throw Unavailable();
                return Utf8.GetString(Bytes(count));
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or OverflowException)
        { throw Unavailable(); }
    }
    private static InvalidOperationException Unavailable() => new("Group historical admission metadata is not available.");
}
