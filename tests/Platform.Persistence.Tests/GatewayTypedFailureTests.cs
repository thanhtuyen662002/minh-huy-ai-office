extern alias RuntimeWorker;

using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GatewayTypedFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyGatewayExceptionRetainsConcreteApiAndFixedClassification(bool transient)
    {
        var inner = new InvalidOperationException("private synthetic detail");
        var legacy = new AiProviderExecutionException("legacy synthetic failure", transient, inner);
        var contract = Assert.IsAssignableFrom<IAiGatewayProviderFailure>(legacy);
        Assert.Equal(transient, contract.IsTransient);
        Assert.Equal("legacy synthetic failure", legacy.Message);
        Assert.Same(inner, legacy.InnerException);
        Assert.Equal(typeof(AiProviderExecutionException), legacy.GetType().GetProperty(nameof(legacy.IsTransient))!.DeclaringType);
        Assert.True(GroupModelAttemptProviderFailure.TryClassify(legacy, out var kind));
        Assert.Equal(transient ? GroupModelAttemptFailure.TransientProvider : GroupModelAttemptFailure.PermanentProvider, kind);
    }

    [Fact]
    public void UnknownLocalAndWrappedFailuresNeverInferProviderOutcomes()
    {
        foreach (var error in new Exception?[] { null, new Exception("private synthetic detail"),
            new InvalidOperationException("private synthetic detail"), new HttpRequestException("private synthetic detail"),
            new TimeoutException("private synthetic detail"),
            new AggregateException(new AiProviderExecutionException("private synthetic detail", true)) })
        {
            Assert.False(GroupModelAttemptProviderFailure.TryClassify(error, out var kind));
            Assert.Equal(default, kind);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationExceptionCannotMasqueradeAsTypedProviderFailure(bool callerCancelled)
    {
        using var cancellation = new CancellationTokenSource();
        if (callerCancelled) cancellation.Cancel();
        var error = new TypedCancellation();
        if (callerCancelled)
            Assert.ThrowsAny<OperationCanceledException>(() => GroupModelAttemptProviderFailure.TryClassify(error, out _, cancellation.Token));
        else
        {
            Assert.False(GroupModelAttemptProviderFailure.TryClassify(error, out var kind));
            Assert.Equal(default, kind);
        }
        Assert.Equal(0, error.Reads);
    }

    [Fact]
    public void ClassificationReadsOnlyTheHostTypedBooleanOnce()
    {
        var error = new TypedError(() => true);
        Assert.True(GroupModelAttemptProviderFailure.TryClassify(error, out var kind));
        Assert.Equal(GroupModelAttemptFailure.TransientProvider, kind);
        Assert.Equal(1, error.Reads);
    }

    [Fact]
    public void CallerCancellationRefusesBeforeEvenReadingTypedMetadata()
    {
        var error = new TypedError(() => throw new Exception("must not read"));
        Assert.ThrowsAny<OperationCanceledException>(() => GroupModelAttemptProviderFailure.TryClassify(error, out _, new CancellationToken(true)));
        Assert.Equal(0, error.Reads);
    }

    [Fact]
    public void CancellationDuringMetadataReadReturnsNoClassification()
    {
        using var cancellation = new CancellationTokenSource();
        var error = new TypedError(() => { cancellation.Cancel(); return true; });
        Assert.ThrowsAny<OperationCanceledException>(() => GroupModelAttemptProviderFailure.TryClassify(error, out _, cancellation.Token));
        Assert.Equal(1, error.Reads);
    }

    [Fact]
    public void FaultyHostMetadataGetterRemainsALocalFault()
    {
        var local = new InvalidOperationException("private synthetic detail");
        var error = new TypedError(() => throw local);
        var observed = Assert.Throws<InvalidOperationException>(() => GroupModelAttemptProviderFailure.TryClassify(error, out _));
        Assert.Same(local, observed);
        Assert.Equal(1, error.Reads);
    }

    private sealed class TypedError(Func<bool> value) : Exception, IAiGatewayProviderFailure
    {
        internal int Reads { get; private set; }
        public bool IsTransient { get { Reads++; return value(); } }
        public override string Message => throw new Exception("Message must not be read");
        public override string ToString() => throw new Exception("ToString must not be read");
    }

    private sealed class TypedCancellation : OperationCanceledException, IAiGatewayProviderFailure
    {
        internal int Reads { get; private set; }
        public bool IsTransient { get { Reads++; throw new Exception("Cancellation must not read provider metadata"); } }
    }
}
