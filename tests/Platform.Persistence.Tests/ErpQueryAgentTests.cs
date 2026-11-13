extern alias RuntimeWorker;

using MinhHuy.AIOffice.Shared.Contracts;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class ErpQueryAgentTests
{
    [Theory]
    [InlineData("SELECT TOP (10) c.Name, COUNT(*) AS n FROM dbo.Customer AS c GROUP BY c.Name")]
    [InlineData("WITH x AS (SELECT Id FROM dbo.Customer) SELECT * FROM x;")]
    [InlineData("select\n  Id, Name\nfrom Customer\nwhere Province = N'Hà Nội'")]
    [InlineData("SELECT c.Id FROM dbo.Customer AS c INNER JOIN dbo.Province AS p ON p.Id = c.ProvinceId WHERE p.Id IS NULL")]
    public void Guard_accepts_single_current_database_select(string sql)
    {
        var statement = ErpAgentSqlGuard.Validate(sql);
        Assert.False(statement.EndsWith(';'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("UPDATE dbo.Customer SET Name = N'x'")]
    [InlineData("DELETE FROM dbo.Customer")]
    [InlineData("INSERT INTO dbo.Customer (Id) VALUES (1)")]
    [InlineData("MERGE dbo.Customer AS t USING dbo.Other AS s ON t.Id = s.Id WHEN MATCHED THEN DELETE")]
    [InlineData("EXEC sp_who")]
    [InlineData("SELECT 1; DROP TABLE dbo.Customer")]
    [InlineData("SELECT * INTO dbo.CustomerCopy FROM dbo.Customer")]
    [InlineData("SELECT * FROM OtherDb.dbo.Customer")]
    [InlineData("SELECT * FROM LinkedServer.OtherDb.dbo.Customer")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x', 'SELECT 1')")]
    [InlineData("SELECT 1 -- comment")]
    [InlineData("SELECT 1 /* comment */")]
    [InlineData("WAITFOR DELAY '00:00:10'")]
    [InlineData("SELECT NEXT VALUE FOR dbo.CustomerSequence")]
    [InlineData("DECLARE @x int = 1")]
    [InlineData("SELECT OtherDb.dbo.fn() AS value")]
    public void Guard_rejects_anything_but_a_single_read_only_select(string sql)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ErpAgentSqlGuard.Validate(sql));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task Agent_explores_schema_runs_only_validated_queries_and_answers()
    {
        var gateway = new ScriptedGateway(
            """{"action":"find_tables","keywords":["tinh"]}""",
            "```json\n{\"action\":\"describe\",\"tables\":[\"dbo.Customer\"]}\n```",
            """{"action":"query","purpose":"Đếm khách thiếu tỉnh","sql":"SELECT COUNT(*) AS n FROM dbo.Customer WHERE ProvinceId IS NULL"}""",
            """{"action":"query","purpose":"Sửa dữ liệu","sql":"DELETE FROM dbo.Customer"}""",
            """{"action":"answer","text":"Có 3 khách hàng chưa có tỉnh. "}""");
        var session = new RecordingSession();

        var result = await new ErpQueryAgent(gateway).RunAsync(Envelope(), "Khách nào bị lệch tỉnh?", Evidence(), session, CancellationToken.None);

        Assert.Equal("Có 3 khách hàng chưa có tỉnh.", result.Answer);
        Assert.Equal(5, result.ModelTurns);
        Assert.Equal(5 * 13, result.InputTokens);
        Assert.Equal(5 * 5, result.OutputTokens);
        Assert.Equal("scripted", result.ProviderId);
        Assert.Equal(new[] { "tinh" }, session.Keywords);
        Assert.Equal(new[] { "dbo.Customer" }, session.Described);
        var executed = Assert.Single(session.Queries);
        Assert.Equal("SELECT COUNT(*) AS n FROM dbo.Customer WHERE ProvinceId IS NULL", executed);
        Assert.Equal(2, result.Queries.Count);
        Assert.Equal(1, result.Queries[0].RowCount);
        Assert.Null(result.Queries[0].Error);
        Assert.StartsWith("rejected:", result.Queries[1].Error, StringComparison.Ordinal);
        Assert.Contains("Query rejected before execution", gateway.Inputs[^1], StringComparison.Ordinal);
        Assert.Contains("n\n3", gateway.Inputs[^1], StringComparison.Ordinal);
        Assert.Equal(5, gateway.RequestIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Agent_retries_one_invalid_reply_then_uses_the_text_as_answer()
    {
        var gateway = new ScriptedGateway("không phải JSON", "Vẫn không phải JSON");
        var session = new RecordingSession();

        var result = await new ErpQueryAgent(gateway).RunAsync(Envelope(), "Doanh thu?", Evidence(), session, CancellationToken.None);

        Assert.Equal("Vẫn không phải JSON", result.Answer);
        Assert.Equal(2, result.ModelTurns);
        Assert.Contains("not one valid JSON object", gateway.Inputs[1], StringComparison.Ordinal);
        Assert.Empty(session.Queries);
    }

    [Fact]
    public async Task Agent_stops_at_turn_and_query_limits()
    {
        var replies = Enumerable.Repeat(
            """{"action":"query","purpose":"lặp","sql":"SELECT TOP (1) Id FROM dbo.Customer"}""",
            ErpQueryAgent.MaximumModelTurns).ToArray();
        var gateway = new ScriptedGateway(replies);
        var session = new RecordingSession();

        var result = await new ErpQueryAgent(gateway).RunAsync(Envelope(), "Lặp mãi", Evidence(), session, CancellationToken.None);

        Assert.Equal(ErpQueryAgent.MaximumModelTurns, result.ModelTurns);
        Assert.Equal(ErpQueryAgent.MaximumQueries, session.Queries.Count);
        Assert.Equal(ErpQueryAgent.MaximumQueries, result.Queries.Count);
        Assert.Contains("last turn", gateway.Inputs[^1], StringComparison.Ordinal);
        Assert.Contains("Query budget", gateway.Inputs[^1], StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(result.Answer));
    }

    [Fact]
    public async Task EarlierTurns_ReachThePromptOnlyWhenTheQuestionContinuesAConversation()
    {
        var gateway = new ScriptedGateway("""{"action":"answer","text":"Tháng 10: 90 triệu."}""",
            """{"action":"answer","text":"Không rõ."}""");
        const string earlier = "[Q1] Doanh thu tháng 11?\n[A1] 120 triệu.\n    SQL used: SELECT SUM(Amount) FROM dbo.Invoice";

        await new ErpQueryAgent(gateway).RunAsync(Envelope(), "Còn tháng trước thì sao?", Evidence(), new RecordingSession(),
            CancellationToken.None, conversation: earlier);
        await new ErpQueryAgent(gateway).RunAsync(Envelope(), "Doanh thu?", Evidence(), new RecordingSession(), CancellationToken.None);

        Assert.Contains("Earlier in this conversation", gateway.Inputs[0], StringComparison.Ordinal);
        Assert.Contains("SELECT SUM(Amount) FROM dbo.Invoice", gateway.Inputs[0], StringComparison.Ordinal);
        Assert.True(gateway.Inputs[0].IndexOf("[Q1]", StringComparison.Ordinal) < gateway.Inputs[0].IndexOf("Còn tháng trước", StringComparison.Ordinal));
        Assert.DoesNotContain("Earlier in this conversation", gateway.Inputs[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_maps_unusable_provider_configuration_to_permanent_failure()
    {
        var gateway = new ScriptedGateway { Failure = new InvalidOperationException("not configured") };

        var error = await Assert.ThrowsAsync<AiProviderExecutionException>(() =>
            new ErpQueryAgent(gateway).RunAsync(Envelope(), "Doanh thu?", Evidence(), new RecordingSession(), CancellationToken.None));

        Assert.False(error.IsTransient);
    }

    private static WorkDispatchEnvelope Envelope() => WorkDispatchEnvelope.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, null, DateTimeOffset.UtcNow);

    private static PilotErpEvidence Evidence() =>
        new("CT01NovaNew", 2, [new PilotErpTableEvidence("dbo", "Customer", 42)]);

    private sealed class ScriptedGateway(params string[] replies) : IAiGateway
    {
        private int next;

        public List<string> Inputs { get; } = [];

        public List<string> RequestIds { get; } = [];

        public Exception? Failure { get; init; }

        public Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Inputs.Add(request.Input);
            RequestIds.Add(request.RequestId);
            var output = replies[Math.Min(next++, replies.Length - 1)];
            return Task.FromResult(new AiGatewayResponse(request.RequestId, request.Capability, output, "scripted-model", 13, 5)
            {
                ProviderId = "scripted"
            });
        }
    }

    private sealed class RecordingSession : IErpQuerySession
    {
        public List<string> Keywords { get; } = [];

        public List<string> Described { get; } = [];

        public List<string> Queries { get; } = [];

        public Task<string> FindTablesAsync(IReadOnlyList<string> keywords, CancellationToken cancellationToken)
        {
            Keywords.AddRange(keywords);
            return Task.FromResult("find_tables:\n- dbo.Customer (table, ~42 rows) matching columns: ProvinceId");
        }

        public Task<string> DescribeTablesAsync(IReadOnlyList<string> tables, CancellationToken cancellationToken)
        {
            Described.AddRange(tables);
            return Task.FromResult("describe dbo.Customer:\n  Id int not null PK\n  ProvinceId int null");
        }

        public Task<ErpAgentQueryResult> QueryAsync(string validatedSql, CancellationToken cancellationToken)
        {
            Queries.Add(validatedSql);
            return Task.FromResult(new ErpAgentQueryResult("1 rows:\nn\n3\n", 1, false, null));
        }
    }
}
