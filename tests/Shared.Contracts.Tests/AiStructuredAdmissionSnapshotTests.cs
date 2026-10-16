using MinhHuyAiOffice.Shared.Contracts;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class AiStructuredAdmissionSnapshotTests
{
    private static string Hash(char c = 'A') => new(c, 64);
    private static AiStructuredAdmissionRoute Route(int ordinal = 0, string provider = "primary", IReadOnlyList<string>? aliases = null,
        string? candidate = null, long window = 100, long input = 13, int framing = 5, int output = 8) =>
        new(ordinal, provider, "configured", Hash(), candidate ?? Hash(), Hash(), window, framing, output, input, aliases ?? ["echo-model"]);
    private static AiStructuredAdmissionSnapshot Snapshot(IReadOnlyList<AiStructuredAdmissionRoute>? routes = null,
        string? gateway = null, string? inputHash = null, string? schemaHash = null, string tokenizer = "scalar-version",
        int inputBytes = 20, int schemaBytes = 30, int inputTokens = 5, int schemaTokens = 3, long? deadline = null) =>
        new(gateway ?? Hash(), deadline ?? TimeSpan.FromSeconds(60).Ticks, inputHash ?? Hash(), inputBytes,
            schemaHash ?? Hash(), schemaBytes, tokenizer, inputTokens, schemaTokens, routes ?? [Route()]);

    [Fact]
    public void Snapshot_copies_ordered_routes_and_aliases_with_stable_canonical_identity()
    {
        var aliases = new[] { "z-echo", "a-echo" };
        var route = Route(aliases: aliases); var routes = new[] { route, Route(1, "backup") };
        var snapshot = Snapshot(routes); var original = snapshot.SnapshotSha256;
        aliases[0] = "replaced"; routes[0] = Route(candidate: Hash('B'));
        Assert.Equal(new[] { "a-echo", "z-echo" }, snapshot.Routes[0].EchoedModels);
        Assert.Equal(original, snapshot.SnapshotSha256);
        Assert.Equal(original, Snapshot([Route(aliases: ["a-echo", "z-echo"]), Route(1, "backup")]).SnapshotSha256);
        Assert.Throws<NotSupportedException>(() => ((IList<AiStructuredAdmissionRoute>)snapshot.Routes)[0] = routes[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.Routes[0].EchoedModels)[0] = "replaced");
        Assert.False((object)snapshot is IAiPreparedStructuredRequest);
        Assert.DoesNotContain("configured", snapshot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("echo", route.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("input")]
    [InlineData("schema")]
    [InlineData("tokenizer")]
    [InlineData("bytes")]
    [InlineData("deadline")]
    [InlineData("candidate")]
    [InlineData("window")]
    public void Changed_original_measurement_or_profile_changes_snapshot_identity(string change)
    {
        var changed = Snapshot(gateway: change == "gateway" ? Hash('B') : null,
            inputHash: change == "input" ? Hash('B') : null, schemaHash: change == "schema" ? Hash('B') : null,
            tokenizer: change == "tokenizer" ? "scalar-next" : "scalar-version", inputBytes: change == "bytes" ? 21 : 20,
            deadline: change == "deadline" ? TimeSpan.FromSeconds(59).Ticks : null,
            routes: [Route(candidate: change == "candidate" ? Hash('B') : null, window: change == "window" ? 99 : 100)]);
        Assert.NotEqual(Snapshot().SnapshotSha256, changed.SnapshotSha256);
    }

    [Theory]
    [InlineData("route-order")]
    [InlineData("duplicate-provider")]
    [InlineData("empty-routes")]
    [InlineData("too-many-routes")]
    [InlineData("input-count")]
    [InlineData("schema-count")]
    [InlineData("input-bytes")]
    [InlineData("schema-bytes")]
    [InlineData("deadline")]
    [InlineData("lowercase-hash")]
    [InlineData("surrogate")]
    public void Invalid_measurement_metadata_is_bounded_and_sanitized(string change)
    {
        var error = Assert.Throws<ArgumentException>(() => Snapshot(
            routes: change == "route-order" ? [Route(1)] : change == "duplicate-provider" ? [Route(), Route(1)]
                : change == "empty-routes" ? [] : change == "too-many-routes" ? [Route(), Route(1, "backup"), Route()] : null,
            inputTokens: change == "input-count" ? 0 : 5, schemaTokens: change == "schema-count" ? 0 : 3,
            inputBytes: change == "input-bytes" ? 128001 : 20, schemaBytes: change == "schema-bytes" ? 32001 : 30,
            deadline: change == "deadline" ? 0 : null, gateway: change == "lowercase-hash" ? Hash('a') : null,
            tokenizer: change == "surrogate" ? "private\uD800" : "scalar-version"));
        Assert.Equal("AI structured admission snapshot metadata is invalid.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("counts-inconsistent")]
    [InlineData("window")]
    [InlineData("overflow")]
    [InlineData("framing")]
    [InlineData("output")]
    [InlineData("alias-duplicate")]
    [InlineData("alias-byte-limit")]
    public void Route_window_and_alias_corruption_cannot_form_a_valid_snapshot(string change)
    {
        Assert.Throws<ArgumentException>(() => Snapshot([Route(input: change == "counts-inconsistent" ? 14 : change == "overflow" ? long.MaxValue : 13,
            window: change == "window" ? 20 : 100, framing: change == "framing" ? -1 : 5,
            output: change == "output" ? 32769 : 8,
            aliases: change == "alias-duplicate" ? ["same", "same"] : change == "alias-byte-limit" ? [new string('中', 86)] : null)]));
    }
}
