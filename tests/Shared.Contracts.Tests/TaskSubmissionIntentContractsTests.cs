using System.Text.Json;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class TaskSubmissionIntentContractsTests
{
    private static readonly Guid Operation = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Source = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("Tồn kho 😀 �", "6354CD8F9D07F489B9F1B213CE89B4FE5EF4016DFC44329AE740B0C39BDE0656")]
    [InlineData("ồ", "653C057F6AFF5A8FF5FEAFA8B8CFAABAE942E33F0605BC03D14876C53B223DE5")]
    [InlineData("o\u0302\u0300", "7176B4A97A12F1F815F8133B9E33C7D021885765A6A5D560976A42104F0ED186")]
    public void FingerprintMatchesIndependentPythonByteVectors(string question, string expected)
    {
        Assert.Equal(expected, TaskSubmissionIntentIdentity.Fingerprint(new(Operation, Source, question)));
    }

    [Fact]
    public void FingerprintBindsExactInputWithoutRenormalizingOrBindingOperation()
    {
        var request = new TaskSubmissionPrepareRequest(Operation, Source, "Tồn kho 😀 �");
        var expected = TaskSubmissionIntentIdentity.Fingerprint(request);
        Assert.Equal(expected, TaskSubmissionIntentIdentity.Fingerprint(request with { OperationId = Guid.NewGuid() }));
        Assert.NotEqual(expected, TaskSubmissionIntentIdentity.Fingerprint(request with { DataSourceId = Guid.NewGuid() }));
        Assert.NotEqual(expected, TaskSubmissionIntentIdentity.Fingerprint(request with { Question = request.Question + "?" }));
        Assert.NotEqual(TaskSubmissionIntentIdentity.Fingerprint(request with { Question = "ồ" }),
            TaskSubmissionIntentIdentity.Fingerprint(request with { Question = "o\u0302\u0300" }));
    }

    [Fact]
    public void OperationKeyPreservesExistingScopedTaskStepAndMessageIdentity()
    {
        var authority = AuthorizationContext.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var key = TaskSubmissionIntentIdentity.IdempotencyKey(Operation);
        Assert.Equal("web-intent-v1-11111111111141118111111111111111", key);
        PilotTaskSubmissionRequest.ValidateIdempotencyKey(key);
        var task = PilotTaskIdentity.ForTask(authority, key);
        var step = PilotTaskIdentity.ForStep(task);
        var message = PilotTaskIdentity.ForMessage(task, step);
        Assert.Equal(task, PilotTaskIdentity.ForTask(authority, TaskSubmissionIntentIdentity.IdempotencyKey(Operation)));
        Assert.Equal(step, PilotTaskIdentity.ForStep(task));
        Assert.Equal(message, PilotTaskIdentity.ForMessage(task, step));
        Assert.NotEqual(task, PilotTaskIdentity.ForTask(authority, TaskSubmissionIntentIdentity.IdempotencyKey(Guid.NewGuid())));
        Assert.NotEqual(task, PilotTaskIdentity.ForTask(AuthorizationContext.Create(authority.TenantId, authority.CompanyId, Guid.NewGuid()), key));
        Assert.NotEqual(task, PilotTaskIdentity.ForTask(AuthorizationContext.Create(authority.TenantId, Guid.NewGuid(), authority.UserId), key));
        Assert.NotEqual(task, PilotTaskIdentity.ForTask(AuthorizationContext.Create(Guid.NewGuid(), authority.CompanyId, authority.UserId), key));
    }

    [Fact]
    public void GuidSpellingDoesNotChangeCanonicalSourceOrOperationBytes()
    {
        var upperSource = Guid.Parse(Source.ToString("D").ToUpperInvariant());
        var request = new TaskSubmissionPrepareRequest(Operation, Source, "Inventory");
        Assert.Equal(TaskSubmissionIntentIdentity.Fingerprint(request),
            TaskSubmissionIntentIdentity.Fingerprint(request with { DataSourceId = upperSource }));
        Assert.Equal(TaskSubmissionIntentIdentity.IdempotencyKey(Operation),
            TaskSubmissionIntentIdentity.IdempotencyKey(Guid.Parse(Operation.ToString("D").ToUpperInvariant())));
    }

    [Fact]
    public void ValidationPreservesTheUtf16LimitAndRejectsAdjacentOverflow()
    {
        var maximum = string.Concat(Enumerable.Repeat("😀", 2000));
        Assert.Equal(4000, maximum.Length);
        var request = new TaskSubmissionPrepareRequest(Operation, Source, maximum);
        request.Validate();
        Assert.True(TaskSubmissionIntentIdentity.IsFingerprint(TaskSubmissionIntentIdentity.Fingerprint(request)));
        Assert.Throws<ArgumentException>(() => (request with { Question = maximum + "x" }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { OperationId = Guid.Empty }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { DataSourceId = Guid.Empty }).Validate());
        Assert.Throws<ArgumentException>(() => TaskSubmissionIntentIdentity.IdempotencyKey(Guid.Empty));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" Inventory")]
    [InlineData("Inventory ")]
    [InlineData("Inventory\n")]
    [InlineData("Inventory\0")]
    [InlineData("Inventory\tcount")]
    public void ValidationRejectsNoncanonicalInput(string question) =>
        Assert.Throws<ArgumentException>(() => new TaskSubmissionPrepareRequest(Operation, Source, question).Validate());

    [Fact]
    public void InvalidStoredScalarCannotBecomeReplacementTextBeforeHashing()
    {
        foreach (var question in new[] { "Inventory" + new string('\ud800', 1), new string('\udc00', 1), "x\ud800y" })
        {
            var request = new TaskSubmissionPrepareRequest(Operation, Source, question);
            Assert.Throws<ArgumentException>(request.Validate);
            Assert.Throws<ArgumentException>(() => TaskSubmissionIntentIdentity.Fingerprint(request));
        }
        Assert.True(TaskSubmissionIntentIdentity.IsFingerprint(TaskSubmissionIntentIdentity.Fingerprint(new(Operation, Source, "Literal �"))));
    }

    [Fact]
    public void ExecuteRequiresExactUppercaseFingerprint()
    {
        var valid = TaskSubmissionIntentIdentity.Fingerprint(new(Operation, Source, "Inventory"));
        new TaskSubmissionExecuteRequest(valid).Validate();
        foreach (var invalid in new[] { null, "", valid.ToLowerInvariant(), valid + "0", " " + valid, new string('G', 64) })
        {
            Assert.False(TaskSubmissionIntentIdentity.IsFingerprint(invalid));
            Assert.Throws<ArgumentException>(() => new TaskSubmissionExecuteRequest(invalid!).Validate());
        }
    }

    [Theory]
    [InlineData("{\"dataSourceId\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"question\":\"Inventory\"}")]
    [InlineData("{\"operationId\":\"11111111-1111-4111-8111-111111111111\",\"question\":\"Inventory\"}")]
    [InlineData("{\"operationId\":\"11111111-1111-4111-8111-111111111111\",\"dataSourceId\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\"}")]
    [InlineData("{\"operationId\":\"11111111-1111-4111-8111-111111111111\",\"dataSourceId\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"question\":\"Inventory\",\"tenantId\":\"caller\"}")]
    [InlineData("{\"operationId\":\"11111111-1111-4111-8111-111111111111\",\"dataSourceId\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"question\":\"Inventory\",\"ownerId\":\"caller\"}")]
    [InlineData("{\"operationId\":\"11111111-1111-4111-8111-111111111111\",\"dataSourceId\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"question\":\"Inventory\",\"maxAttempts\":16}")]
    public void PrepareJsonRejectsMissingInputAndCallerAuthority(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TaskSubmissionPrepareRequest>(json, Json));

    [Fact]
    public void PrepareJsonRoundTripPreservesExactSupplementaryAndReplacementText()
    {
        var request = new TaskSubmissionPrepareRequest(Operation, Source, "Tồn kho 😀 �");
        Assert.Equal(request, JsonSerializer.Deserialize<TaskSubmissionPrepareRequest>(JsonSerializer.Serialize(request, Json), Json));
    }
}
