using System.Data;
using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class GroupCommittedTerminalProbeTests
{
    [Theory]
    [InlineData("guarded")]
    [InlineData("non-sql")]
    [InlineData("cancelled")]
    [InlineData("dirty")]
    [InlineData("ambient")]
    [InlineData("mars")]
    [InlineData("foreign-tenant")]
    [InlineData("foreign-company")]
    [InlineData("empty-batch")]
    [InlineData("null-keys")]
    [InlineData("null-clock")]
    public async Task TerminalProbeUsesCurrentSqlAuthorityBeforeKeysAndPreservesExistingEntryGuards(string fault)
    {
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var worker = new GroupExtractionWorkerBinding(scope.TenantId, scope.CompanyId, Guid.NewGuid(), 1);
        if (fault == "foreign-tenant") worker = worker with { TenantId = Guid.NewGuid() };
        if (fault == "foreign-company") worker = worker with { CompanyId = Guid.NewGuid() };
        var trap = new ForbiddenSql();
        var options = new DbContextOptionsBuilder<PlatformDbContext>();
        if (fault == "non-sql") options.UseInMemoryDatabase(Guid.NewGuid().ToString());
        else options.UseSqlServer("Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Encrypt=true;Connect Timeout=1;MultipleActiveResultSets="
            + (fault == "mars" ? "true" : "false")).AddInterceptors(trap);
        using var db = new PlatformDbContext(options.Options); var keys = new ForbiddenKeys();
        if (fault == "dirty") db.Add(new GroupBatchTerminalReceiptRecord());
        using var caller = new CancellationTokenSource(); if (fault == "cancelled") caller.Cancel();
        using var ambient = fault == "ambient" ? new TransactionScope(TransactionScopeAsyncFlowOption.Enabled) : null;
        var store = new GroupTerminalFrontierStore(db, worker, fault == "null-clock" ? null! : TimeProvider.System,
            fault == "null-keys" ? null! : keys);
        Task Action() => store.TryAdvanceCommittedAsync(scope, fault == "empty-batch" ? Guid.Empty : Guid.NewGuid(), caller.Token);
        if (fault == "cancelled")
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(Action); Assert.Equal(caller.Token, error.CancellationToken);
        }
        else if (fault.StartsWith("foreign-", StringComparison.Ordinal)) await Assert.ThrowsAsync<UnauthorizedAccessException>(Action);
        else await Assert.ThrowsAsync<InvalidOperationException>(Action);
        Assert.Equal(fault == "guarded" ? 1 : 0, trap.Attempts); Assert.Equal(0, keys.Calls);
        if (fault != "non-sql") Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        Assert.Null(db.Database.CurrentTransaction);
        // No actual SQL absence/terminal/advance or key release is qualified.
    }

    [Fact]
    public void TerminalProbeTranslatesBoundedFourKeyMetadataWithoutManifestOrPrivateSource()
    {
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); var batch = Guid.NewGuid();
        var trap = new ForbiddenSql(); var keys = new ForbiddenKeys();
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer(
            "Server=127.0.0.1,1;Database=never_connect;Integrated Security=true;Encrypt=true;MultipleActiveResultSets=false").AddInterceptors(trap).Options);
        var store = new GroupTerminalFrontierStore(db, new(scope.TenantId, scope.CompanyId, Guid.NewGuid(), 1), TimeProvider.System, keys);
        var sql = store.CommittedTerminalRows(scope, batch).ToQueryString();
        new TSql160Parser(true).Parse(new StringReader(sql), out var errors); Assert.Empty(errors);
        Assert.Contains("GroupBatchTerminalReceipts", sql); Assert.Contains("TOP(@", sql); Assert.Contains("= 2;", sql);
        foreach (var field in new[] { "TenantId", "CompanyId", "BindingId", "BatchId" }) Assert.Contains("[" + field + "] =", sql);
        foreach (var id in new[] { scope.TenantId, scope.CompanyId, scope.SourceBindingId, batch }) Assert.Contains(id.ToString("D"), sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Manifest", sql); Assert.DoesNotContain("GroupMessage", sql); Assert.DoesNotContain("GroupRequest", sql);
        Assert.Throws<InvalidOperationException>(() => store.CommittedTerminalRows(scope, Guid.Empty));
        Assert.Equal(0, trap.Attempts); Assert.Equal(0, keys.Calls);
    }

    private sealed class ForbiddenSql : DbConnectionInterceptor
    {
        internal int Attempts;
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData data,
            InterceptionResult result, CancellationToken cancellationToken = default)
        { Attempts++; throw new InvalidOperationException("Owned terminal probe attempted SQL."); }
    }
    private sealed class ForbiddenKeys : IGroupSourceKeyProvider
    {
        internal int Calls;
        public ValueTask<GroupSourceKeyMaterial> ResolveWriteAsync(GroupScope scope, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Owned terminal probe must not resolve keys."); }
        public ValueTask<GroupSourceKeyMaterial> ResolveReadAsync(GroupScope scope, string keyId, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Owned terminal probe must not resolve keys."); }
    }
}
