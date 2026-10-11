using System.Text.Json;
using MinhHuy.AIOffice.Platform.Persistence;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupSecretQuarantineTests
{
    [Theory]
    [InlineData("Server=owned.invalid;User Id=fixture;Password=PRIVATE_SENTINEL_72691")]
    [InlineData("{\"client_secret\": \"PRIVATE_SENTINEL_72691\"}")]
    [InlineData("api-key: PRIVATE_SENTINEL_72691")]
    [InlineData("access token = PRIVATE_SENTINEL_72691")]
    [InlineData("\"refresh_token\": \"PRIVATE_SENTINEL_72691\"")]
    [InlineData("Ｓｅｃｒｅｔ＝PRIVATE_SENTINEL_72691")]
    [InlineData("pass\u200Bword = PRIVATE_SENTINEL_72691")]
    [InlineData("pass\u0000word = PRIVATE_SENTINEL_72691")]
    [InlineData("PWD=PRIVATE_SENTINEL_72691")]
    [InlineData("connection string: PRIVATE_SENTINEL_72691")]
    [InlineData("accountkey = PRIVATE_SENTINEL_72691")]
    public void AssignmentsAndInspectionOnlyUnicodeViewQuarantineWholeField(string input)
    {
        var decision = GroupSecretQuarantine.Inspect(input);
        Assert.True(decision.RequiresQuarantine);
        Assert.Equal(GroupSecretQuarantineReason.CredentialAssignment, decision.Reason);
        Assert.DoesNotContain("PRIVATE_SENTINEL_72691", decision.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_SENTINEL_72691", JsonSerializer.Serialize(decision), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authorization: Basic PRIVATE_SENTINEL_72691", GroupSecretQuarantineReason.Authorization)]
    [InlineData("{\"Authorization\": \"Basic abc\"}", GroupSecretQuarantineReason.Authorization)]
    [InlineData("Bearer PRIVATE_SENTINEL_72691", GroupSecretQuarantineReason.Authorization)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nPRIVATE_SENTINEL_72691", GroupSecretQuarantineReason.PrivateKey)]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", GroupSecretQuarantineReason.PrivateKey)]
    [InlineData("-----BEGIN PGP PRIVATE KEY BLOCK-----", GroupSecretQuarantineReason.PrivateKey)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJvd25lZEZpeHR1cmUiOnRydWV9.PRIVATE_SENTINEL_72691", GroupSecretQuarantineReason.SignedToken)]
    [InlineData("sk-proj-PRIVATE_SENTINEL_72691", GroupSecretQuarantineReason.ProviderCredential)]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", GroupSecretQuarantineReason.ProviderCredential)]
    [InlineData("postgres://fixture:PRIVATE_SENTINEL_72691@owned.invalid/db", GroupSecretQuarantineReason.CredentialUri)]
    [InlineData("https://owned.invalid/object?sig=PRIVATE_SENTINEL_72691", GroupSecretQuarantineReason.SignedUri)]
    [InlineData("https://owned.invalid/object?X-Amz-Signature=PRIVATE_SENTINEL_72691", GroupSecretQuarantineReason.SignedUri)]
    [InlineData("<Password>PRIVATE_SENTINEL_72691</Password>", GroupSecretQuarantineReason.XmlCredential)]
    public void KnownCredentialFormsReturnMetadataOnly(string input, GroupSecretQuarantineReason reason)
    {
        var decision = GroupSecretQuarantine.Inspect(input);
        Assert.True(decision.RequiresQuarantine);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal("group-secret-quarantine-v1", decision.PolicyVersion);
        Assert.DoesNotContain(input, decision.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(input, JsonSerializer.Serialize(decision), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Tra cứu tồn kho, nhập xuất mã SP-00012, kho HCM.")]
    [InlineData("Quên mật khẩu đăng nhập; cần hỗ trợ reset, chưa gửi mật khẩu.")]
    [InlineData("Token hết hạn khi nhập kho, mã lỗi HTTP 401.")]
    [InlineData("https://owned.invalid/help?module=stock&error=401")]
    [InlineData("Khách yêu cầu ngày mai nhưng IT chưa xác nhận SLA.")]
    [InlineData("Cảm ơn 😀\uFEFF ")]
    [InlineData("")]
    public void BusinessTextIsRetainedVerbatimAndNoMatchIsExplicitlyLimited(string input)
    {
        var original = input;
        var decision = GroupSecretQuarantine.Inspect(input);
        Assert.False(decision.RequiresQuarantine);
        Assert.Equal(GroupSecretQuarantineReason.NoMatch, decision.Reason);
        Assert.Equal(original, input);
    }

    [Fact]
    public void DecodedModelFieldsMustBeInspectedAfterJsonUnescape()
    {
        using var output = JsonDocument.Parse("{\"problem\":\"pass\\u0077ord=PRIVATE_SENTINEL_72691\"}");
        Assert.True(GroupSecretQuarantine.Inspect(output.RootElement.GetProperty("problem").GetString()).RequiresQuarantine);
        Assert.True(GroupSecretQuarantine.InspectOutputJson(output.RootElement.GetRawText()).RequiresQuarantine);
    }

    [Theory]
    [InlineData("{\"notes\":[{\"problem\":\"pass\\u0077ord=PRIVATE_SENTINEL_72691\"}]}")]
    [InlineData("{\"notes\":[{\"missing_fields\":[\"Authorization: Basic PRIVATE_SENTINEL_72691\"]}]}")]
    [InlineData("{\"notes\":[{\"requested_deadline_text\":\"https://owned.invalid?sig=PRIVATE_SENTINEL_72691\"}]}")]
    [InlineData("{\"sk-PRIVATE_SENTINEL_72691\":true}")]
    public void WholeDecodedOutputIncludingNestedFieldsAndKeysIsQuarantinedWithoutPrivateResult(string output)
    {
        var decision = GroupSecretQuarantine.InspectOutputJson(output);
        Assert.True(decision.RequiresQuarantine);
        Assert.DoesNotContain("PRIVATE_SENTINEL_72691", decision.ToString());
        Assert.DoesNotContain("PRIVATE_SENTINEL_72691", JsonSerializer.Serialize(decision));
    }

    [Theory]
    [InlineData("{\"pass\\u0077ord\":\"\"}")]
    [InlineData("{\"password\":null}")]
    [InlineData("{\"password\":false}")]
    [InlineData("{\"password\":[]}")]
    [InlineData("{\"password\":{\"value\":\"PRIVATE_SENTINEL_72691\"}}")]
    [InlineData("{\"ｐａｓｓｗｏｒｄ\":\"PRIVATE_SENTINEL_72691\"}")]
    [InlineData("{\"pass\\u200Bword\":\"PRIVATE_SENTINEL_72691\"}")]
    [InlineData("{\"Authorization\":\"Ba\\u0073ic PRIVATE_SENTINEL_72691\"}")]
    public void DecodedAssignmentsKeepAllValueKindsAndUnicodeKeyFences(string output)
    {
        var decision = GroupSecretQuarantine.InspectOutputJson(output);
        Assert.True(decision.RequiresQuarantine);
        Assert.DoesNotContain("PRIVATE_SENTINEL_72691", decision.ToString());
        Assert.DoesNotContain("PRIVATE_SENTINEL_72691", JsonSerializer.Serialize(decision));
    }

    [Theory]
    [InlineData("{\"requested_deadline_text\":null}")]
    [InlineData("{\"password_reset\":\"Quên mật khẩu, cần hỗ trợ reset\"}")]
    [InlineData("{\"notes\":[{\"quantity\":123456,\"verified\":false,\"problem\":\"Tra cứu tồn kho\"}]}")]
    public void AssignmentInspectionPreservesUnrelatedBusinessValues(string output)
    {
        Assert.False(GroupSecretQuarantine.InspectOutputJson(output).RequiresQuarantine);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"PRIVATE_SENTINEL_72691\"")]
    [InlineData("{malformed PRIVATE_SENTINEL_72691}")]
    public void InvalidStructuredOutputFailsClosedWithMetadataOnly(string? output)
    {
        var decision = GroupSecretQuarantine.InspectOutputJson(output);
        Assert.True(decision.RequiresQuarantine); Assert.Equal(GroupSecretQuarantineReason.InvalidInput, decision.Reason);
        Assert.DoesNotContain("PRIVATE_SENTINEL_72691", decision.ToString());
    }

    [Fact]
    public void StructuredOutputBoundsAndLegitimateNullableBusinessFields()
    {
        Assert.False(GroupSecretQuarantine.InspectOutputJson("{\"notes\":[{\"problem\":\"Tra cứu tồn kho\",\"requested_deadline_text\":null,\"missing_fields\":[],\"verified\":false}]}").RequiresQuarantine);
        Assert.Equal(GroupSecretQuarantineReason.InvalidInput,
            GroupSecretQuarantine.InspectOutputJson(new string(' ', GroupSecretQuarantine.MaximumTextLength + 1)).Reason);
        var nodes = "{\"notes\":[" + string.Join(',', Enumerable.Repeat("0", 1025)) + "]}";
        Assert.Equal(GroupSecretQuarantineReason.InvalidInput, GroupSecretQuarantine.InspectOutputJson(nodes).Reason);
        var depth = "{\"notes\":" + new string('[', 13) + "0" + new string(']', 13) + "}";
        Assert.Equal(GroupSecretQuarantineReason.InvalidInput, GroupSecretQuarantine.InspectOutputJson(depth).Reason);
    }

    [Fact]
    public void MalformedUtf16CannotBeReplacedIntoAcceptedStructuredOutput()
    {
        foreach (var output in new[] { "{\"title\":\"" + new string((char)0xD800, 1) + "\"}",
            "{\"title\":\"\\uD800\"}", "{\"\\uDC00\":true}", "{\"title\":\"\\uD800x\"}" })
            Assert.Equal(GroupSecretQuarantineReason.InvalidInput, GroupSecretQuarantine.InspectOutputJson(output).Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InvalidInputCannotBecomeNoMatch(int kind)
    {
        // Construct invalid UTF16 at execution time: test-case serialization
        // replaces unpaired-surrogate string attributes with valid U+FFFD.
        var input = kind == 0 ? null : new string((char)(kind == 1 ? 0xD800 : 0xDC00), 1);
        var decision = GroupSecretQuarantine.Inspect(input);
        Assert.True(decision.RequiresQuarantine);
        Assert.Equal(GroupSecretQuarantineReason.InvalidInput, decision.Reason);
    }

    [Fact]
    public void FixedMaximumAndNormalizationExpansionCannotBypassBounds()
    {
        Assert.False(GroupSecretQuarantine.Inspect(new string('a', GroupSecretQuarantine.MaximumTextLength)).RequiresQuarantine);
        Assert.Equal(GroupSecretQuarantineReason.InvalidInput,
            GroupSecretQuarantine.Inspect(new string('a', GroupSecretQuarantine.MaximumTextLength + 1)).Reason);
        Assert.Equal(GroupSecretQuarantineReason.InvalidInput,
            GroupSecretQuarantine.Inspect(new string('\uFDFA', 4000)).Reason);
        var sensitive = new string('a', GroupSecretQuarantine.MaximumTextLength - 35) + " password=PRIVATE_SENTINEL_72691";
        Assert.Equal(GroupSecretQuarantineReason.CredentialAssignment, GroupSecretQuarantine.Inspect(sensitive).Reason);
    }

    [Fact]
    public void CallersCannotForgeOrMutateNoMatchReceipt()
    {
        Assert.Empty(typeof(GroupSecretQuarantineDecision).GetConstructors());
        Assert.All(typeof(GroupSecretQuarantineDecision).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.DoesNotContain(typeof(GroupSecretQuarantineDecision).GetProperties(), property => property.PropertyType == typeof(string)
            && property.Name != nameof(GroupSecretQuarantineDecision.PolicyVersion));
    }
}
