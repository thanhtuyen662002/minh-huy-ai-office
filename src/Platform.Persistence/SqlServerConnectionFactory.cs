using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class SqlServerConnectionFactory : ISqlConnectionFactory
{
    public DbConnection Create(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new SqlConnection(connectionString);
    }
}
