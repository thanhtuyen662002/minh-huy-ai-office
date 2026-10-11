using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// PRIVATE/COLD prototype. Constructs commands only; never opens a connection or executes SQL.
// The owned orchestrator must authenticate the original root, guard both databases and execute
// the entire seed plan in one pinned Serializable transaction under the original source APPLOCK.
internal static class GroupShadowCursorSql
{
    internal const string PredecessorMigration = "20261011061648_AddGroupTerminalKeyEnrollment";
    internal const string CursorMigration = "20261011075456_AddGroupSignedTerminalCursor";
    internal const int MaximumSnapshotRowsPerTable = 16384;
    internal const int MaximumSnapshotJsonBytes = 8388608;
    // These three tables are owned by versioned migrations and have no
    // EF entity. Keep every column and the original primary-key ordering.
    // Cold tests reconcile this inventory with the actual full migration SQL.
    private readonly record struct MigrationColumn(string Name, string StoreType, bool Nullable = false);
    private static readonly (string Name, MigrationColumn[] Columns, string[] Key)[] MigrationOwnedTables =
    [
        ("CustomerAiCreditSettlements", [new("SettlementId", "nvarchar(200)"), new("ReservationId", "nvarchar(200)"),
            new("TenantId", "nvarchar(200)"), new("CompanyId", "nvarchar(200)"), new("AuthorityVersion", "bigint"),
            new("PricingPolicyId", "nvarchar(200)"), new("PricingPolicyVersion", "bigint"), new("SettledAiCredits", "bigint"),
            new("ReleasedAiCredits", "bigint")], ["SettlementId"]),
        ("CustomerSlaPriorityAdmissions", [new("AdmissionId", "nvarchar(200)"), new("TenantId", "nvarchar(200)"),
            new("CompanyId", "nvarchar(200)"), new("UserId", "nvarchar(200)"), new("AuthorityVersion", "bigint"),
            new("PolicyId", "nvarchar(200)"), new("PolicyVersion", "bigint"), new("ServiceClass", "nvarchar(200)"),
            new("SchedulerPriority", "int"), new("CreatedAtUtc", "datetimeoffset"), new("PriorityCeiling", "int")], ["AdmissionId"]),
        ("ToolExecutionAudit", [new("AuditId", "uniqueidentifier"), new("TenantId", "uniqueidentifier"),
            new("CompanyId", "uniqueidentifier"), new("UserId", "uniqueidentifier"), new("TaskId", "uniqueidentifier"),
            new("Resource", "nvarchar(200)"), new("Action", "nvarchar(100)"), new("Risk", "nvarchar(40)"),
            new("Authorized", "bit"), new("DecisionReason", "nvarchar(200)"), new("OccurredAtUtc", "datetimeoffset"),
            new("ExecutionId", "nvarchar(200)", true)], ["AuditId"])
    ];
    private const string SourceName = "AIOfficeLocal";
    private const string Schema = "aioffice";
    private static readonly string[] SeedOrder = ["Companies", "GroupConnectorAccounts", "GroupServices",
        "GroupBindings", "GroupSourceStates", "GroupBatchAllocations", "GroupBatchClaimReceipts",
        "GroupBatchTerminalReceipts", "GroupTerminalAdvanceKeys", "GroupTerminalAdvances", "GroupTerminalFrontierStates"];

    internal sealed record RootIdentity(Guid TenantId, Guid CompanyId, Guid SourceId, Guid ServiceId, Guid BatchId);

    internal sealed class ShadowDatabase
    {
        internal string Name { get; }
        private ShadowDatabase(string name) => Name = name;
        internal static ShadowDatabase Create() => new("GroupCursorShadow_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
    }

    internal sealed record CommandTemplate(string Tag, SqlCommand Command);
    internal sealed record SeedPlan(CommandTemplate Guard, IReadOnlyList<CommandTemplate> Copies);
    internal static ShadowDatabase NewShadow() => ShadowDatabase.Create();

    internal static string RequireShadowName(string name)
    {
        const string prefix = "GroupCursorShadow_";
        if (name is null || name.Length != prefix.Length + 32 || !name.StartsWith(prefix, StringComparison.Ordinal)
            || name.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef")) throw Refused();
        return name;
    }

    internal static SeedPlan BuildSeedPlan(PlatformDbContext database, ShadowDatabase shadow, RootIdentity identity)
    {
        RequireIdentity(identity);
        var target = RequireShadowName(shadow?.Name!);
        var model = RequireModel(database);
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(["GroupTerminalFrontierStates"]);
        while (queue.TryDequeue(out var name))
        {
            if (!closure.Add(name)) continue;
            foreach (var fk in model.GetRelationalModel().Tables.Single(x => x.Name == name).ForeignKeyConstraints)
                if (fk.PrincipalTable.Name != name) queue.Enqueue(fk.PrincipalTable.Name);
        }
        if (!closure.SetEquals(SeedOrder)) throw Refused();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in SeedOrder)
        {
            if (model.GetRelationalModel().Tables.Single(x => x.Name == name).ForeignKeyConstraints
                .Any(x => x.PrincipalTable.Name != name && !seen.Contains(x.PrincipalTable.Name))) throw Refused();
            seen.Add(name);
        }

        var guards = SeedUnitGuard() + SchemaGuard(model, target) + RootGuard();
        var copies = SeedOrder.Select(name =>
        {
            var table = model.GetRelationalModel().Tables.Single(x => x.Name == name);
            var columns = string.Join(",", table.Columns.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => Quote(x.Name)));
            var from = Table(SourceName, name) + " AS s WITH (UPDLOCK,HOLDLOCK) WHERE " + Predicate(name);
            var sql = SeedUnitGuard() + $"""
                IF (SELECT COUNT_BIG(*) FROM {from})<>1 THROW 51052,N'Owned shadow source row refused',1;
                IF EXISTS(SELECT 1 FROM {Table(target, name)} WITH (UPDLOCK,HOLDLOCK)) THROW 51052,N'Owned shadow target row refused',1;
                INSERT INTO {Table(target, name)}({columns}) SELECT {columns} FROM {from};
                IF @@ROWCOUNT<>1 THROW 51052,N'Owned shadow copy refused',1;
                IF (SELECT COUNT_BIG(*) FROM {Table(target, name)} WITH (UPDLOCK,HOLDLOCK))<>1 THROW 51052,N'Owned shadow copied row refused',1;
                """;
            return Template("copy:" + name, sql, identity);
        }).ToArray();
        return new(Template("guard:schema-and-root", guards, identity), Array.AsReadOnly(copies));
    }

    // Uses all EF tables, unmapped migration-owned tables and EF history. Unknown/missing user
    // tables refuse instead of disappearing from a digest. Each result is only tag + 64 hex hash.
    // JSON is generated privately by SQL and bounded after construction; this is NOT a streaming
    // SQL-allocation bound. The orchestrator must bound duration/SQL memory and compare ALL tags.
    internal static IReadOnlyList<CommandTemplate> SourceSnapshots(PlatformDbContext database) => Snapshots(database, SourceName);
    internal static IReadOnlyList<CommandTemplate> ShadowSnapshots(PlatformDbContext database, ShadowDatabase shadow) =>
        Snapshots(database, RequireShadowName(shadow?.Name!));

    private static IReadOnlyList<CommandTemplate> Snapshots(PlatformDbContext database, string name)
    {
        var model = RequireModel(database);
        var tables = model.GetRelationalModel().Tables.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        if (tables.Length is < 11 or > 128 || tables.Any(x => x.Schema != Schema || x.PrimaryKey is null)) throw Refused();
        var names = tables.Select(x => x.Name).Concat(MigrationOwnedTables.Select(x => x.Name)).Append("__EFMigrationsHistory").ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw Refused();
        var inventory = string.Join(",", names.Order(StringComparer.Ordinal).Select(x => "(N'" + x + "')"));
        var prefix = $"""
            IF DB_ID(N'{name}') IS NULL THROW 51053,N'Owned shadow database refused',1;
            IF EXISTS(SELECT s.name,t.name FROM [{name}].sys.tables t JOIN [{name}].sys.schemas s ON s.schema_id=t.schema_id
              WHERE t.is_ms_shipped=0 EXCEPT SELECT N'aioffice',v.Name FROM(VALUES {inventory})v(Name))
              OR EXISTS(SELECT N'aioffice',v.Name FROM(VALUES {inventory})v(Name) EXCEPT
              SELECT s.name,t.name FROM [{name}].sys.tables t JOIN [{name}].sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0)
              THROW 51053,N'Owned shadow inventory refused',1;
            """;
        var inventoryHash = Hash("inventory", $"SELECT s.name AS [Schema],t.name AS [Table] FROM [{name}].sys.tables t JOIN [{name}].sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0 ORDER BY s.name,t.name");
        var result = new List<CommandTemplate> { Template("inventory", prefix + inventoryHash) };
        foreach (var table in tables)
        {
            var columns = string.Join(",", table.Columns.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => Quote(x.Name)));
            var order = string.Join(",", table.PrimaryKey!.Columns.Select(x => Quote(x.Name)));
            result.Add(Template("rows:" + table.Name, RowHash(name, table.Name, columns, order)));
        }
        foreach (var table in MigrationOwnedTables)
        {
            var columns = string.Join(",", table.Columns.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => Quote(x.Name)));
            var order = string.Join(",", table.Key.Select(Quote));
            result.Add(Template("rows:" + table.Name, MigrationTableGuard(name, table.Name, table.Columns, table.Key)
                + RowHash(name, table.Name, columns, order)));
        }
        result.Add(Template("rows:__EFMigrationsHistory", RowHash(name, "__EFMigrationsHistory", "[MigrationId],[ProductVersion]", "[MigrationId]")));
        foreach (var item in CatalogQueries(name)) result.Add(Template("catalog:" + item.Tag, Hash("catalog:" + item.Tag, item.Sql)));
        return result.AsReadOnly();
    }

    private static string MigrationTableGuard(string database, string table, MigrationColumn[] columns, string[] key)
    {
        var expected = string.Join(",", columns.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x =>
            $"(N'{x.Name}',N'{CanonicalType(x.StoreType)}',CAST({(x.Nullable ? 1 : 0)} AS bit),CAST(0 AS bit))"));
        var wanted = $"SELECT v.Name,v.StoreType,v.Nullable,v.UserDefined FROM(VALUES {expected})v(Name,StoreType,Nullable,UserDefined)";
        var actual = ColumnShape(database, table, full: false);
        var p = $"[{database}].sys.";
        var actualKey = $"SELECT c.name,ic.key_ordinal FROM {p}indexes i JOIN {p}index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id "
            + $"JOIN {p}columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id JOIN {p}tables t ON t.object_id=i.object_id "
            + $"JOIN {p}schemas s ON s.schema_id=t.schema_id WHERE s.name=N'aioffice' AND t.name=N'{table}' AND i.is_primary_key=1 AND ic.key_ordinal>0";
        var expectedKey = string.Join(",", key.Select((name, index) => $"(N'{name}',{index + 1})"));
        var wantedKey = $"SELECT v.Name,v.Ordinal FROM(VALUES {expectedKey})v(Name,Ordinal)";
        return $"IF EXISTS({actual} EXCEPT {wanted}) OR EXISTS({wanted} EXCEPT {actual}) THROW 51053,N'Owned shadow unmapped columns refused',1;\n"
            + $"IF EXISTS({actualKey} EXCEPT {wantedKey}) OR EXISTS({wantedKey} EXCEPT {actualKey}) THROW 51053,N'Owned shadow unmapped key refused',1;\n";
    }

    private static string RowHash(string database, string table, string columns, string order) => $"""
        IF (SELECT COUNT_BIG(*) FROM {Table(database, table)} WITH (HOLDLOCK))>{MaximumSnapshotRowsPerTable}
          THROW 51054,N'Owned shadow snapshot rows refused',1;
        {Hash("rows:" + table, $"SELECT {columns} FROM {Table(database, table)} WITH (HOLDLOCK) ORDER BY {order}")}
        """;

    private static string Hash(string tag, string sql) => $"""
        DECLARE @payload nvarchar(max)=({sql} FOR JSON PATH,INCLUDE_NULL_VALUES);
        IF @payload IS NULL OR DATALENGTH(@payload)>{MaximumSnapshotJsonBytes} THROW 51054,N'Owned shadow snapshot bytes refused',1;
        SELECT N'{tag}' AS [Tag],CONVERT(char(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),@payload)),2) AS [Sha256];
        """;

    private static (string Tag, string Sql)[] CatalogQueries(string database)
    {
        var p = $"[{database}].sys.";
        return [
            ("objects", $"SELECT s.name AS [Schema],o.* FROM {p}objects o JOIN {p}schemas s ON s.schema_id=o.schema_id WHERE o.is_ms_shipped=0 ORDER BY s.name,o.name,o.object_id"),
            ("columns", $"SELECT s.name AS [Schema],o.name AS [Object],c.*,ty.name AS [Type],dc.definition AS [DefaultDefinition],cc.definition AS [ComputedDefinition] FROM {p}columns c JOIN {p}objects o ON o.object_id=c.object_id JOIN {p}schemas s ON s.schema_id=o.schema_id JOIN {p}types ty ON ty.user_type_id=c.user_type_id LEFT JOIN {p}default_constraints dc ON dc.object_id=c.default_object_id LEFT JOIN {p}computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id WHERE o.is_ms_shipped=0 ORDER BY s.name,o.name,c.column_id"),
            ("checks", $"SELECT s.name AS [Schema],o.name AS [Table],c.* FROM {p}check_constraints c JOIN {p}objects o ON o.object_id=c.parent_object_id JOIN {p}schemas s ON s.schema_id=o.schema_id WHERE c.is_ms_shipped=0 ORDER BY s.name,o.name,c.name"),
            ("foreignkeys", $"SELECT fk.*,fc.constraint_column_id,fc.parent_column_id,fc.referenced_column_id,ps.name AS [ParentSchema],pt.name AS [ParentTable],pc.name AS [ParentColumn],rs.name AS [ReferenceSchema],rt.name AS [ReferenceTable],rc.name AS [ReferenceColumn] FROM {p}foreign_keys fk JOIN {p}foreign_key_columns fc ON fc.constraint_object_id=fk.object_id JOIN {p}tables pt ON pt.object_id=fc.parent_object_id JOIN {p}schemas ps ON ps.schema_id=pt.schema_id JOIN {p}columns pc ON pc.object_id=pt.object_id AND pc.column_id=fc.parent_column_id JOIN {p}tables rt ON rt.object_id=fc.referenced_object_id JOIN {p}schemas rs ON rs.schema_id=rt.schema_id JOIN {p}columns rc ON rc.object_id=rt.object_id AND rc.column_id=fc.referenced_column_id WHERE fk.is_ms_shipped=0 ORDER BY ps.name,pt.name,fk.name,fc.constraint_column_id"),
            ("indexes", $"SELECT s.name AS [Schema],t.name AS [Table],i.*,ic.index_column_id,ic.column_id,ic.key_ordinal,ic.partition_ordinal,ic.is_descending_key,ic.is_included_column,c.name AS [Column] FROM {p}indexes i JOIN {p}tables t ON t.object_id=i.object_id JOIN {p}schemas s ON s.schema_id=t.schema_id LEFT JOIN {p}index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id LEFT JOIN {p}columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id WHERE t.is_ms_shipped=0 ORDER BY s.name,t.name,i.index_id,ic.index_column_id"),
            ("modules", $"SELECT s.name AS [Schema],o.name AS [Object],m.* FROM {p}sql_modules m JOIN {p}objects o ON o.object_id=m.object_id JOIN {p}schemas s ON s.schema_id=o.schema_id WHERE o.is_ms_shipped=0 ORDER BY s.name,o.name"),
            ("triggers", $"SELECT tr.* FROM {p}triggers tr WHERE tr.is_ms_shipped=0 ORDER BY tr.parent_class,tr.parent_id,tr.name"),
            ("permissions", $"SELECT dp.*,gr.name AS [Grantee],ga.name AS [Grantor] FROM {p}database_permissions dp JOIN {p}database_principals gr ON gr.principal_id=dp.grantee_principal_id JOIN {p}database_principals ga ON ga.principal_id=dp.grantor_principal_id ORDER BY dp.class,dp.major_id,dp.minor_id,dp.grantee_principal_id,dp.grantor_principal_id,dp.type,dp.state"),
            ("principals", $"SELECT pr.* FROM {p}database_principals pr ORDER BY pr.principal_id"),
            ("roles", $"SELECT rm.* FROM {p}database_role_members rm ORDER BY rm.role_principal_id,rm.member_principal_id"),
            ("schemas", $"SELECT s.* FROM {p}schemas s ORDER BY s.schema_id"),
            ("properties", $"SELECT ep.class,ep.class_desc,ep.major_id,ep.minor_id,ep.name,CONVERT(nvarchar(128),SQL_VARIANT_PROPERTY(ep.value,'BaseType')) AS [BaseType],CONVERT(int,SQL_VARIANT_PROPERTY(ep.value,'Precision')) AS [Precision],CONVERT(int,SQL_VARIANT_PROPERTY(ep.value,'Scale')) AS [Scale],CONVERT(int,SQL_VARIANT_PROPERTY(ep.value,'MaxLength')) AS [MaxLength],CONVERT(nvarchar(128),SQL_VARIANT_PROPERTY(ep.value,'Collation')) AS [Collation],CONVERT(varbinary(8000),ep.value) AS [ValueBytes] FROM {p}extended_properties ep ORDER BY ep.class,ep.major_id,ep.minor_id,ep.name"),
            ("database", $"SELECT d.name,d.owner_sid,d.collation_name,d.compatibility_level,d.is_read_only,d.is_trustworthy_on,d.is_db_chaining_on FROM master.sys.databases d WHERE d.name=N'{database}' ORDER BY d.name")
        ];
    }

    private static string SchemaGuard(IModel model, string target)
    {
        var statements = new List<string>
        {
            $"IF DB_ID(N'{SourceName}') IS NULL OR DB_ID(N'{target}') IS NULL OR DB_ID(N'{SourceName}')=DB_ID(N'{target}') THROW 51051,N'Owned shadow catalogs refused',1;",
            $"IF NOT EXISTS(SELECT 1 FROM {Table(target, "__EFMigrationsHistory")} WHERE MigrationId=N'{PredecessorMigration}') OR EXISTS(SELECT 1 FROM {Table(target, "__EFMigrationsHistory")} WHERE MigrationId>N'{PredecessorMigration}') THROW 51051,N'Owned shadow predecessor refused',1;"
        };
        foreach (var name in SeedOrder)
        {
            var table = model.GetRelationalModel().Tables.Single(x => x.Name == name && x.Schema == Schema);
            var expected = string.Join(",", table.Columns.OrderBy(x => x.Name, StringComparer.Ordinal)
                .Select(x => $"(N'{x.Name}',N'{CanonicalType(x.StoreType)}',CAST({(x.IsNullable ? 1 : 0)} AS bit),CAST(0 AS bit))"));
            foreach (var database in new[] { SourceName, target })
            {
                var actual = ColumnShape(database, name, full: false);
                var wanted = $"SELECT v.Name,v.StoreType,v.Nullable,v.UserDefined FROM(VALUES {expected})v(Name,StoreType,Nullable,UserDefined)";
                statements.Add($"IF EXISTS({actual} EXCEPT {wanted}) OR EXISTS({wanted} EXCEPT {actual}) THROW 51051,N'Owned shadow model columns refused',1;");
            }
            var before = ColumnShape(SourceName, name, full: true);
            var after = ColumnShape(target, name, full: true);
            statements.Add($"IF EXISTS({before} EXCEPT {after}) OR EXISTS({after} EXCEPT {before}) THROW 51051,N'Owned shadow actual column schema refused',1;");
        }
        return string.Join(Environment.NewLine, statements) + Environment.NewLine;
    }

    private static string ColumnShape(string database, string table, bool full)
    {
        var p = $"[{database}].sys.";
        var shape = "c.name,CASE WHEN ty.name IN(N'nvarchar',N'nchar') THEN ty.name+N'('+CASE WHEN c.max_length=-1 THEN N'max' ELSE CONVERT(nvarchar(10),c.max_length/2) END+N')' "
            + "WHEN ty.name IN(N'varchar',N'char',N'varbinary',N'binary') THEN ty.name+N'('+CASE WHEN c.max_length=-1 THEN N'max' ELSE CONVERT(nvarchar(10),c.max_length) END+N')' "
            + "WHEN ty.name IN(N'decimal',N'numeric') THEN ty.name+N'('+CONVERT(nvarchar(10),c.precision)+N','+CONVERT(nvarchar(10),c.scale)+N')' "
            + "WHEN ty.name IN(N'datetime2',N'datetimeoffset',N'time') THEN ty.name+N'('+CONVERT(nvarchar(10),c.scale)+N')' "
            + "WHEN ty.name=N'float' THEN ty.name+N'('+CONVERT(nvarchar(10),c.precision)+N')' ELSE ty.name END,c.is_nullable,ty.is_user_defined";
        if (full) shape += ",c.column_id,c.max_length,c.precision,c.scale,c.collation_name,c.is_identity,c.is_computed,c.is_rowguidcol,c.is_sparse,c.is_column_set,c.generated_always_type,c.encryption_type,dc.definition,cc.definition,cc.is_persisted";
        return $"SELECT {shape} FROM {p}columns c JOIN {p}tables t ON t.object_id=c.object_id JOIN {p}schemas s ON s.schema_id=t.schema_id JOIN {p}types ty ON ty.user_type_id=c.user_type_id LEFT JOIN {p}default_constraints dc ON dc.object_id=c.default_object_id LEFT JOIN {p}computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id WHERE s.name=N'aioffice' AND t.name=N'{table}'";
    }

    private static string CanonicalType(string type) => type switch
    {
        "datetimeoffset" => "datetimeoffset(7)",
        "datetime2" => "datetime2(7)",
        "time" => "time(7)",
        "float" => "float(53)",
        _ => type
    };

    private static string RootGuard() => $"""
        IF (SELECT COUNT_BIG(*) FROM {Table(SourceName, "GroupTerminalAdvances")} AS s WITH (UPDLOCK,HOLDLOCK)
          WHERE {Predicate("GroupTerminalAdvances")})<>1 THROW 51052,N'Owned shadow root refused',1;
        IF NOT EXISTS(SELECT 1 FROM {Table(SourceName, "GroupTerminalAdvances")} AS a WITH (UPDLOCK,HOLDLOCK)
          JOIN {Table(SourceName, "GroupTerminalFrontierStates")} AS c WITH (UPDLOCK,HOLDLOCK)
          ON c.TenantId=a.TenantId AND c.CompanyId=a.CompanyId AND c.BindingId=a.BindingId
            AND c.Version=a.Version AND c.ThroughSequence=a.ThroughSequence AND c.LastTerminalBatchId=a.BatchId AND c.UpdatedAtUtc=a.AdvancedAtUtc
          JOIN {Table(SourceName, "GroupBatchTerminalReceipts")} AS t WITH (UPDLOCK,HOLDLOCK)
          ON t.TenantId=a.TenantId AND t.CompanyId=a.CompanyId AND t.BindingId=a.BindingId AND t.BatchId=a.BatchId
            AND t.OperationId=a.TerminalOperationId AND t.AfterSequence=a.AfterSequence AND t.ThroughSequence=a.ThroughSequence
            AND t.ManifestSha256=a.TerminalManifestSha256 AND t.CommittedAtUtc=a.TerminalCommittedAtUtc AND t.ServiceId=a.ServiceId
          WHERE a.TenantId=@tenant AND a.CompanyId=@company AND a.BindingId=@source AND a.ServiceId=@service AND a.BatchId=@batch
            AND a.Version=1 AND a.AfterSequence=0 AND a.PreviousVersion IS NULL AND a.PreviousThroughSequence IS NULL
            AND a.PreviousFingerprint IS NULL AND a.PreviousAdvancedAtUtc IS NULL AND DATEPART(tz,a.AdvancedAtUtc)=0)
          THROW 51052,N'Owned shadow original tuple refused',1;
        """;

    private static string SeedUnitGuard() => """
        IF DB_NAME()<>N'AIOfficeLocal' OR @@TRANCOUNT<>1 OR XACT_STATE()<>1
          OR NOT EXISTS(SELECT 1 FROM sys.dm_exec_sessions WHERE session_id=@@SPID AND transaction_isolation_level=4)
          THROW 51051,N'Owned shadow seed transaction refused',1;
        DECLARE @sourceResource nvarchar(255)=N'aioffice:group-ingest:'+
          LOWER(REPLACE(CONVERT(char(36),@tenant),'-',''))+N'/'+
          LOWER(REPLACE(CONVERT(char(36),@company),'-',''))+N'/'+LOWER(REPLACE(CONVERT(char(36),@source),'-',''));
        IF ISNULL(APPLOCK_MODE(N'public',@sourceResource,N'Transaction'),N'')<>N'Exclusive'
          THROW 51051,N'Owned shadow seed source lock refused',1;

        """;

    private static string Predicate(string name)
    {
        var scope = "s.TenantId=@tenant AND s.CompanyId=@company";
        var source = scope + " AND s.BindingId=@source";
        var full = source + " AND s.ServiceId=@service";
        var terminal = $"SELECT t.ClaimOperationId FROM {Table(SourceName, "GroupBatchTerminalReceipts")} t WITH (UPDLOCK,HOLDLOCK) WHERE t.TenantId=@tenant AND t.CompanyId=@company AND t.BindingId=@source AND t.ServiceId=@service AND t.BatchId=@batch";
        var key = $"SELECT a.KeyId FROM {Table(SourceName, "GroupTerminalAdvances")} a WITH (UPDLOCK,HOLDLOCK) WHERE a.TenantId=@tenant AND a.CompanyId=@company AND a.BindingId=@source AND a.ServiceId=@service AND a.BatchId=@batch AND a.Version=1";
        return name switch
        {
            "Companies" => "s.TenantId=@tenant AND s.Id=@company",
            "GroupConnectorAccounts" => scope + $" AND s.Id=(SELECT b.ConnectorAccountId FROM {Table(SourceName, "GroupBindings")} b WITH (UPDLOCK,HOLDLOCK) WHERE b.TenantId=@tenant AND b.CompanyId=@company AND b.Id=@source)",
            "GroupServices" => scope + " AND s.Id=@service",
            "GroupBindings" => scope + " AND s.Id=@source",
            "GroupSourceStates" => source,
            "GroupBatchAllocations" => full + " AND s.Id=@batch",
            "GroupBatchClaimReceipts" => full + $" AND s.BatchId=@batch AND s.OperationId=({terminal})",
            "GroupBatchTerminalReceipts" => full + " AND s.BatchId=@batch",
            "GroupTerminalAdvanceKeys" => full + $" AND s.KeyId=({key})",
            "GroupTerminalAdvances" => full + " AND s.BatchId=@batch AND s.Version=1 AND s.AfterSequence=0 AND s.PreviousVersion IS NULL AND s.PreviousThroughSequence IS NULL AND s.PreviousFingerprint IS NULL AND s.PreviousAdvancedAtUtc IS NULL",
            "GroupTerminalFrontierStates" => source + " AND s.LastTerminalBatchId=@batch AND s.Version=1",
            _ => throw Refused()
        };
    }

    private static IModel RequireModel(PlatformDbContext database)
    {
        if (database is null || database.Database.GetDbConnection().State != ConnectionState.Closed || database.ChangeTracker.HasChanges()) throw Refused();
        return database.GetService<IDesignTimeModel>().Model;
    }

    private static void RequireIdentity(RootIdentity identity)
    {
        if (identity is null || new[] { identity.TenantId, identity.CompanyId, identity.SourceId, identity.ServiceId, identity.BatchId }.Any(x => x == Guid.Empty)) throw Refused();
    }

    private static CommandTemplate Template(string tag, string sql, RootIdentity? identity = null)
    {
        var command = new SqlCommand(sql) { CommandTimeout = 30 };
        if (identity is not null)
            foreach (var item in new[] { ("@tenant", identity.TenantId), ("@company", identity.CompanyId), ("@source", identity.SourceId), ("@service", identity.ServiceId), ("@batch", identity.BatchId) })
                command.Parameters.Add(new SqlParameter(item.Item1, SqlDbType.UniqueIdentifier) { Value = item.Item2 });
        return new(tag, command);
    }

    private static string Quote(string fixedName) => "[" + fixedName.Replace("]", "]]", StringComparison.Ordinal) + "]";
    private static string Table(string database, string table) => Quote(database) + ".[aioffice]." + Quote(table);
    private static InvalidOperationException Refused() => new("Owned shadow SQL template refused");
}
