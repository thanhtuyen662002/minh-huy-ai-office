extern alias RuntimeWorker;

using System.Security.Cryptography;
using System.Text;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Actual existing gate/catalog/counter classes with synthetic metadata only.
// No prepared request, HTTP, source/SQL authority or model quality is proved.
[Collection(nameof(BoundedTokenizationCollection))]
public sealed class StructuredModelPlanningTests
{
    private const string Schema = "{\"type\":\"object\",\"properties\":{},\"required\":[],\"additionalProperties\":false}";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Fact]
    public async Task PlanningUsesCompleteSchemaAndEveryCurrentRouteWithoutProviderExecutionOrPreparedAuthority()
    {
        var f = new Fixture(); var first = await f.Gate.DescribePlanningAsync(Schema); var second = await f.Gate.DescribePlanningAsync(Schema);
        Assert.Equal(f.Gateway.DescribeStructuredGateway().MetadataSha256, first.GatewaySha256); Assert.Equal(Hash(Schema), first.SchemaSha256);
        Assert.Equal("model-0", first.ConfiguredPrimaryModel); Assert.Equal(6000 - 2048 - 128 - 10, first.MaximumInputTextTokens);
        Assert.Equal(first.ProfileSetSha256, second.ProfileSetSha256); Assert.Equal(2, f.Counts); Assert.All(f.Adapters, a => Assert.Equal(0, a.Executions));
        Assert.Equal(StructuredTextTokenCounter.Version, first.TokenizerVersion);
        Assert.False(typeof(IAiPreparedStructuredRequest).IsAssignableFrom(first.GetType()));
        Assert.DoesNotContain(first.ConfiguredPrimaryModel, first.ToString());
    }

    [Fact]
    public async Task EmptyProductionCatalogCannotInventAWindowOrModelPlanningProfile()
    {
        var f = new Fixture(); await Denied(() => new StructuredModelAdmission(f.Gateway).DescribePlanningAsync(Schema));
        Assert.Equal(0, f.Counts); Assert.All(f.Adapters, a => Assert.Equal(0, a.Executions));
    }

    [Theory]
    [InlineData("primary-revoke")]
    [InlineData("backup-revoke")]
    [InlineData("metadata-drift")]
    public async Task PlanningRechecksReleaseAndGatewayAfterSchemaCounterAwait(string fault)
    {
        var f = new Fixture(); f.DuringCount = () =>
        {
            if (fault == "metadata-drift") f.Adapters[1].Changed = true;
            else f.Catalog.RevokeForSyntheticControls(f.Profiles[fault == "primary-revoke" ? 0 : 1].CandidateSha256);
        };
        await Denied(() => f.Gate.DescribePlanningAsync(Schema)); Assert.Equal(1, f.Counts); Assert.All(f.Adapters, a => Assert.Equal(0, a.Executions));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("missing-contract")]
    [InlineData("invalid-utf16")]
    public async Task InvalidSchemaNeverReachesCounterOrProvider(string fault)
    {
        var f = new Fixture(); var schema = fault switch { "malformed" => "{", "oversized" => new string('x', 40000), "missing-contract" => "{}", _ => Schema + '\ud800' };
        await Denied(() => f.Gate.DescribePlanningAsync(schema)); Assert.Equal(0, f.Counts); Assert.All(f.Adapters, a => Assert.Equal(0, a.Executions));
    }

    [Fact]
    public async Task SchemaOutputAndFramingMustLeaveInputCapacityOnEveryRoute()
    {
        var f = new Fixture { Tokens = 100000 }; await Denied(() => f.Gate.DescribePlanningAsync(Schema)); Assert.Equal(1, f.Counts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanningPreservesOriginalCallerCancellationBeforeOrDuringCounter(bool during)
    {
        var f = new Fixture(); using var caller = new CancellationTokenSource();
        if (during) f.DuringCount = caller.Cancel; else caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Gate.DescribePlanningAsync(Schema, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(during ? 1 : 0, f.Counts);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public async Task PlanningNeverReturnsAfterMonotonicRollbackOrOriginalPreparationBudget(int elapsed)
    {
        var clock = new PlanningClock(); var f = new Fixture(clock: clock); f.DuringCount = () => clock.Timestamp += TimeSpan.FromSeconds(elapsed).Ticks;
        await Denied(() => f.Gate.DescribePlanningAsync(Schema)); Assert.Equal(1, f.Counts);
    }

    [Fact]
    public async Task CounterPrivateFailureIsFixedAndCannotBecomeProviderOrPlanningSuccess()
    {
        var f = new Fixture(); f.DuringCount = () => throw new Exception("PRIVATE_PLANNING_COUNTER_CANARY");
        await Denied(() => f.Gate.DescribePlanningAsync(Schema)); Assert.Equal(1, f.Counts);
    }

    [Fact]
    public async Task DifferentSchemaCannotBorrowReleasedProfileMetadata()
    {
        var f = new Fixture(profileSchema: new('B', 64)); await Denied(() => f.Gate.DescribePlanningAsync(Schema)); Assert.Equal(0, f.Counts);
    }

    [Theory]
    [InlineData("schema-hash")]
    [InlineData("model")]
    [InlineData("budget")]
    public void PlanningMetadataStructuralBoundsNeverPreservePrivateInvalidValues(string fault)
    {
        var error = Assert.Throws<ArgumentException>(() => new AiStructuredPlanningMetadata(new('A', 64),
            fault == "schema-hash" ? "PRIVATE_HASH_CANARY" : new('B', 64), new('C', 64), "tokenizer",
            fault == "model" ? " PRIVATE_MODEL_CANARY " : "model", fault == "budget" ? 0 : 100));
        Assert.Equal("Structured planning metadata is invalid.", error.Message); Assert.Null(error.InnerException);
    }

    private static async Task Denied(Func<Task> operation)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(operation);
        Assert.Equal(StructuredModelAdmission.UnavailableMessage, error.Message); Assert.Null(error.InnerException);
    }
    private sealed class PlanningClock : TimeProvider
    {
        internal long Timestamp = TimeSpan.FromHours(1).Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
    private sealed class Fixture
    {
        internal readonly PlanningAdapter[] Adapters = [new(0), new(1)];
        internal readonly OrderedFailoverAiGateway Gateway;
        internal readonly StructuredModelProfileCandidate[] Profiles;
        internal readonly StructuredModelProfileCatalog Catalog;
        internal readonly StructuredModelAdmission Gate;
        internal int Counts, Tokens = 10;
        internal Action? DuringCount;
        internal Fixture(string? profileSchema = null, TimeProvider? clock = null)
        {
            Gateway = new(Adapters[0], Adapters[1]); var metadata = Gateway.DescribeStructuredGateway();
            Profiles = metadata.Routes.Select((route, i) => new StructuredModelProfileCandidate(new AiModelProfile(route.ProviderId, route.ConfiguredModel,
                new HashSet<AiCapability> { AiCapability.StructuredGeneration }, 1, 0, 1), metadata.MetadataSha256, i,
                route.EndpointIdentitySha256, profileSchema ?? Hash(Schema), StructuredTextTokenCounter.Version, i == 0 ? 10000 : 6000,
                128, 2048, [route.ConfiguredModel], new('D', 64))).ToArray();
            Catalog = StructuredModelProfileCatalog.ForSyntheticControls(Profiles);
            Gate = new(Gateway, Catalog, new BoundedStructuredTextTokenCounter((text, _) =>
            { Assert.Equal(Schema, text); Counts++; DuringCount?.Invoke(); return Tokens; }, TimeSpan.FromSeconds(1)), clock);
        }
    }
    private sealed class PlanningAdapter(int index) : IAiProviderAdapter, IAiStructuredProviderMetadataSource
    {
        internal bool Changed;
        internal int Executions;
        public string ProviderId => "planning-" + index;
        public bool Supports(AiCapability capability) => capability == AiCapability.StructuredGeneration;
        public AiStructuredRouteMetadata DescribeStructuredRoute() => new(ProviderId, "model-" + index + (Changed ? "-changed" : ""),
            new((char)('A' + index), 64), 2048, TimeSpan.FromSeconds(60), new("planning-fixture-v1", 128000, 32000, 64000, 262144, 32, 10, 4));
        public Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
        { Executions++; throw new InvalidOperationException("Planning must never execute a provider."); }
    }
}
