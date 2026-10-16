using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

// Only the test executable uses this operator connection. No shipping client,
// secret loader or customer configuration is introduced by this proof.
internal static class GroupCursorShadowGuard
{
    internal const string OwnershipProperty = "aioffice.owned-group-cursor-shadow";
    internal const string ExactOwnershipPredicate = """
        CONVERT(sysname,SQL_VARIANT_PROPERTY(value,'BaseType'))=N'nvarchar'
        AND CONVERT(int,SQL_VARIANT_PROPERTY(value,'MaxLength'))=64
        AND DATALENGTH(CONVERT(nvarchar(max),value))=64
        AND CONVERT(nvarchar(max),value) COLLATE Latin1_General_100_BIN2=@owner COLLATE Latin1_General_100_BIN2
        """;
    private static readonly HashSet<string> AllowedConnectionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Server", "Data Source", "Database", "Initial Catalog", "User ID", "UID", "Password", "PWD",
        "Encrypt", "TrustServerCertificate", "Trust Server Certificate"
    };

    internal static string RequireOperatorConnection(string? connection)
    {
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException();
        try
        {
            var keys = new DbConnectionStringBuilder { ConnectionString = connection };
            if (keys.Keys.Cast<string>().Any(key => !AllowedConnectionKeys.Contains(key))) throw new InvalidOperationException();
            var parsed = new SqlConnectionStringBuilder(connection);
            if (parsed.DataSource != "sql" || parsed.InitialCatalog != "AIOfficeLocal" || parsed.UserID != "sa"
                || parsed.IntegratedSecurity || parsed.Authentication != SqlAuthenticationMethod.NotSpecified
                || parsed.Encrypt != SqlConnectionEncryptOption.Mandatory || !parsed.TrustServerCertificate
                || parsed.Password.Length is < 32 or > 128
                || parsed.Password.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
                throw new InvalidOperationException();
            return Normalized(parsed.Password, "AIOfficeLocal");
        }
        catch (Exception error) when (error is ArgumentException or FormatException or KeyNotFoundException)
        {
            // The original parser exception may contain operator credentials.
            throw new InvalidOperationException();
        }
    }

    internal static string DatabaseName(string suffix)
    {
        if (!Regex.IsMatch(suffix, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant)
            || !Guid.TryParseExact(suffix, "N", out var owner) || owner == Guid.Empty) throw new InvalidOperationException();
        return "GroupCursorShadow_" + suffix;
    }

    internal static string DatabaseConnection(string trustedOperator, string name)
    {
        var trusted = new SqlConnectionStringBuilder(trustedOperator);
        using var unopened = new SqlConnection(trustedOperator);
        RequireCatalog(unopened, "AIOfficeLocal");
        if (name != "master") RequireShadowName(name);
        trusted.InitialCatalog = name;
        return trusted.ConnectionString;
    }

    internal static void RequireShadowName(string name)
    {
        const string prefix = "GroupCursorShadow_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name != DatabaseName(name[prefix.Length..]))
            throw new InvalidOperationException();
    }

    internal static void RequireShadowConnection(SqlConnection connection)
    {
        var name = new SqlConnectionStringBuilder(connection.ConnectionString).InitialCatalog;
        RequireShadowName(name); RequireCatalog(connection, name);
    }

    internal static SqlCommand CreateDatabase(SqlConnection master, string name)
    {
        RequireShadowName(name); RequireCatalog(master, "master");
        var command = new SqlCommand("""
            IF DB_ID(@database) IS NOT NULL THROW 51070, 'Owned cursor shadow already exists.', 1;
            DECLARE @sql nvarchar(max)=N'CREATE DATABASE '+QUOTENAME(@database);
            EXEC(@sql);
            SELECT CAST(DB_ID(@database) AS int);
            """, master) { CommandTimeout = 30 };
        command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = name;
        return command;
    }

    internal static SqlCommand MarkOwnership(SqlConnection shadow, string name, Guid owner)
    {
        RequireShadowName(name); RequireCatalog(shadow, name); RequireOwner(name, owner);
        var command = new SqlCommand("EXEC sys.sp_addextendedproperty @name=@property,@value=@owner;", shadow) { CommandTimeout = 30 };
        command.Parameters.Add("@property", SqlDbType.NVarChar, 128).Value = OwnershipProperty;
        command.Parameters.Add("@owner", SqlDbType.NVarChar, 32).Value = owner.ToString("N");
        return command;
    }

    internal static SqlCommand DropDatabase(SqlConnection master, string name, Guid owner, int databaseId)
    {
        RequireShadowName(name); RequireOwner(name, owner); RequireCatalog(master, "master");
        if (databaseId <= 4) throw new InvalidOperationException();
        // All identifiers originate in the strict prefix/hex grammar above;
        // values, including ownership and DB_ID, remain typed parameters.
        var command = new SqlCommand($"""
            IF DB_ID(@database) IS NULL OR DB_ID(@database)<>@id
                THROW 51071, 'Owned cursor shadow identity changed.', 1;
            IF NOT EXISTS(SELECT 1 FROM [{name}].sys.extended_properties
                WHERE class=0 AND major_id=0 AND minor_id=0 AND name=@property
                AND {ExactOwnershipPredicate})
                THROW 51072, 'Owned cursor shadow ownership changed.', 1;
            ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            DROP DATABASE [{name}];
            IF DB_ID(@database) IS NOT NULL THROW 51073, 'Owned cursor shadow remains.', 1;
            SELECT 1;
            """, master) { CommandTimeout = 30 };
        command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = name;
        command.Parameters.Add("@id", SqlDbType.Int).Value = databaseId;
        command.Parameters.Add("@property", SqlDbType.NVarChar, 128).Value = OwnershipProperty;
        command.Parameters.Add("@owner", SqlDbType.NVarChar, 32).Value = owner.ToString("N");
        return command;
    }

    internal static SqlCommand InspectOwnership(SqlConnection master, string name, Guid owner)
    {
        RequireShadowName(name); RequireOwner(name, owner); RequireCatalog(master, "master");
        // Bind the closed inspection statement as data. Dynamic execution is
        // required because an absent database must not bind its sys catalog.
        var inspection = $"""
            SELECT CAST(DB_ID(@database) AS int) AS [DatabaseId],CASE
                WHEN EXISTS(SELECT 1 FROM [{name}].sys.extended_properties
                    WHERE class=0 AND major_id=0 AND minor_id=0 AND name=@property AND {ExactOwnershipPredicate}) THEN 1
                WHEN NOT EXISTS(SELECT 1 FROM [{name}].sys.extended_properties
                    WHERE class=0 AND major_id=0 AND minor_id=0 AND name=@property) THEN 0
                ELSE -1 END AS [OwnerState];
            """;
        var command = new SqlCommand("""
            IF DB_ID(@database) IS NULL SELECT CAST(NULL AS int) AS [DatabaseId],CAST(0 AS int) AS [OwnerState];
            ELSE EXEC sys.sp_executesql @inspection,N'@database nvarchar(128),@property nvarchar(128),@owner nvarchar(32)',
                @database=@database,@property=@property,@owner=@owner;
            """, master) { CommandTimeout = 30 };
        command.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = name;
        command.Parameters.Add("@property", SqlDbType.NVarChar, 128).Value = OwnershipProperty;
        command.Parameters.Add("@owner", SqlDbType.NVarChar, 32).Value = owner.ToString("N");
        command.Parameters.Add("@inspection", SqlDbType.NVarChar, -1).Value = inspection;
        return command;
    }

    private static void RequireOwner(string name, Guid owner)
    {
        if (owner == Guid.Empty || name != DatabaseName(owner.ToString("N"))) throw new InvalidOperationException();
    }

    private static void RequireCatalog(SqlConnection connection, string catalog)
    {
        // Construct lifecycle commands before opening. SqlClient intentionally
        // removes the password from ConnectionString after an open connection.
        if (connection.State != ConnectionState.Closed) throw new InvalidOperationException();
        var parsed = new SqlConnectionStringBuilder(connection.ConnectionString);
        if (parsed.Password.Length is < 32 or > 128 || parsed.Password.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))
            || connection.ConnectionString != Normalized(parsed.Password, catalog))
            throw new InvalidOperationException();
    }

    private static string Normalized(string password, string catalog) => new SqlConnectionStringBuilder
    {
        DataSource = "sql",
        InitialCatalog = catalog,
        UserID = "sa",
        Password = password,
        Encrypt = true,
        TrustServerCertificate = true,
        ConnectTimeout = 10,
        ConnectRetryCount = 0,
        Pooling = false,
        MultipleActiveResultSets = false
    }.ConnectionString;
}
