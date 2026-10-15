using System.Security.Cryptography;
using System.Text;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Agent.Worker;

internal sealed partial class StructuredModelAdmission
{
    // Schema-only planning metadata through the same actual Gateway/catalog/
    // counter/clock. No input, prepared handle, HTTP or release is produced.
    public async Task<AiStructuredPlanningMetadata> DescribePlanningAsync(string responseSchema, CancellationToken caller = default)
    {
        caller.ThrowIfCancellationRequested(); var began = clock.GetTimestamp();
        using var timeout = new CancellationTokenSource(PreparationBudget, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, timeout.Token);
        try
        {
            if (responseSchema is null || responseSchema.Length > StructuredResponsesPolicy.SchemaByteLimit) throw Unavailable();
            var bytes = StructuredResponsesPolicy.BoundedText(responseSchema, StructuredResponsesPolicy.SchemaByteLimit); string schemaHash;
            try
            {
                using var parsed = StructuredResponsesPolicy.Parse(bytes); StructuredResponsesPolicy.ValidateSchema(parsed.RootElement);
                schemaHash = Convert.ToHexString(SHA256.HashData(bytes));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            var metadata = gateway.DescribeStructuredGateway(); var profiles = catalog.Resolve(metadata, schemaHash);
            catalog.RequireCurrent(metadata, profiles);
            var schemaTokens = await counter.CountTextAsync(responseSchema, linked.Token).ConfigureAwait(false);
            RequireTime(); if (schemaTokens <= 0) throw Unavailable();
            var maximum = profiles.Min(profile => checked(profile.ContextWindowTokens - profile.MaxOutputTokens - profile.FramingReserveTokens - schemaTokens));
            if (maximum <= 0) throw Unavailable();
            var budget = (int)Math.Min(maximum, int.MaxValue);
            using var stream = new MemoryStream();
            try
            {
                using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
                writer.Write("ai-structured-planning-v1"); writer.Write(metadata.MetadataSha256); writer.Write(schemaHash);
                writer.Write(StructuredTextTokenCounter.Version); writer.Write(budget); writer.Write(schemaTokens); writer.Write(profiles.Count);
                foreach (var profile in profiles) writer.Write(profile.CandidateSha256);
                writer.Flush(); var profileHash = Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
                var result = new AiStructuredPlanningMetadata(metadata.MetadataSha256, schemaHash, profileHash,
                    StructuredTextTokenCounter.Version, profiles[0].ConfiguredModel, budget);
                catalog.RequireCurrent(metadata, profiles);
                if (gateway.DescribeStructuredGateway().MetadataSha256 != metadata.MetadataSha256) throw Unavailable();
                RequireTime(); return result;
            }
            finally { CryptographicOperations.ZeroMemory(stream.GetBuffer()); }

            void RequireTime()
            {
                caller.ThrowIfCancellationRequested(); var elapsed = clock.GetElapsedTime(began);
                if (timeout.IsCancellationRequested || elapsed < TimeSpan.Zero || elapsed >= PreparationBudget) throw Unavailable();
            }
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { throw new OperationCanceledException(caller); }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Unavailable(); }
    }
}
