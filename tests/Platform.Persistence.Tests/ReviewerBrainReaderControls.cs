using System.Collections;
using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed partial class GroupBatchSourceReaderTests
{
    private sealed class CountMismatchIds(Guid[] values) : IReadOnlyList<Guid>
    {
        public int Count => 1;
        public Guid this[int index] => values[index];
        public IEnumerator<Guid> GetEnumerator() => ((IEnumerable<Guid>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public async Task ReviewerBrainActualSelectionMustRemainBoundedAfterSnapshot()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var ids = new List<Guid>();
        for (int index = 0; index < 21; index++) ids.Add((await SeedBrainAsync(f, claim, source)).Request.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => BrainReader(f).ReadAsync(claim, new CountMismatchIds(ids.ToArray()), []));
        Assert.Equal(0, f.Keys.Reads);
    }

    [Fact]
    public async Task ReviewerBrainForeignServiceCannotBorrowAnotherServiceSealedHandle()
    {
        using var f = new Fixture(); var source = await f.CommitAsync(); var claim = await f.ClaimAsync();
        var seed = await SeedBrainAsync(f, claim, source);
        var reader = new GroupBrainCurrentReader(f.Auth.Db, f.Worker with { ServiceId = Guid.NewGuid() }, f.Auth.Clock, f.Keys, new());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadAsync(claim, [seed.Request.Id], []));
        Assert.Equal(0, f.Keys.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewerActualUnmappedSqlQueryMapsEnumsNullOriginsAndCiphertextWithoutOpeningSql(bool glossary)
    {
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var id = Guid.NewGuid(); var user = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var table = new DataTable();
        (string, Type)[] columns = [("TenantId",typeof(Guid)),("CompanyId",typeof(Guid)),("BindingId",typeof(Guid)),
            ("RecordId",typeof(Guid)),("Revision",typeof(long)),("Origin",typeof(int)),("VerificationLevel",typeof(int)),
            ("AuthorServiceId",typeof(Guid)),("AuthorUserId",typeof(Guid)),("SourceBatchId",typeof(Guid)),("ClaimEpoch",typeof(long)),
            ("SourceVersion",typeof(long)),("DeletionGeneration",typeof(long)),("ContentKeyId",typeof(string)),
            ("EnvelopeSha256",typeof(string)),("CreatedAtUtc",typeof(DateTimeOffset)),("ProtectedContent",typeof(byte[]))];
        foreach (var (name, type) in columns) table.Columns.Add(name, type);
        var cipher = new byte[30]; cipher[0] = 1;
        table.Rows.Add(scope.TenantId, scope.CompanyId, scope.SourceBindingId, id, 1L, 2, 2, DBNull.Value,
            user, DBNull.Value, DBNull.Value, 1L, 0L, "owned", new string('A', 64), now, cipher);
        var interceptor = new InertBrainSqlRows(table);
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=Inert_Reviewer_Translation;Integrated Security=true")
            .AddInterceptors(interceptor).Options;
        await using var db = new PlatformDbContext(options);
        using var f = new Fixture();
        var reader = new GroupBrainCurrentReader(db, f.Worker, f.Auth.Clock, f.Keys, new());
        var method = typeof(GroupBrainCurrentReader).GetMethod(glossary ? "GlossaryRevisionAsync" : "RequestRevisionAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task<GroupBrainStoredRevision?>)method.Invoke(reader, [scope, id, 1L, CancellationToken.None])!;
        var mapped = await task;
        Assert.NotNull(mapped); Assert.Equal(id, mapped.RecordId); Assert.Equal(GroupRequestRevisionOrigin.ItEdited, mapped.Origin);
        Assert.Null(mapped.AuthorServiceId); Assert.Null(mapped.SourceBatchId); Assert.Null(mapped.ClaimEpoch);
        Assert.Equal(cipher, mapped.ProtectedContent); Assert.Equal(user, mapped.AuthorUserId);
        Assert.Contains("DATALENGTH(ProtectedContent) BETWEEN 30 AND 64029", interceptor.Command);
        foreach (var coordinate in new[] { "TenantId", "CompanyId", "BindingId", "RecordId", "Revision" })
            Assert.Contains("].[" + coordinate + "]", interceptor.Command);
        Assert.Equal(1, interceptor.OpenSuppressed); Assert.Equal(1, interceptor.ReadSuppressed);
    }

    private sealed class InertBrainSqlRows(DataTable rows) : DbConnectionInterceptor, IDbCommandInterceptor
    {
        internal int OpenSuppressed, ReadSuppressed;
        internal string Command = "";
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        { OpenSuppressed++; return ValueTask.FromResult(InterceptionResult.Suppress()); }
        public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            ReadSuppressed++; Command = command.CommandText;
            var outer = Command[..Command.IndexOf("FROM", StringComparison.Ordinal)];
            var order = System.Text.RegularExpressions.Regex.Matches(outer, @"\[[^\]]+\]\.\[([^\]]+)\]")
                .Select(match => match.Groups[1].Value).ToArray();
            Assert.Equal(rows.Columns.Count, order.Length);
            var projected = rows.DefaultView.ToTable(false, order);
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(projected.CreateDataReader()));
        }
    }
}
