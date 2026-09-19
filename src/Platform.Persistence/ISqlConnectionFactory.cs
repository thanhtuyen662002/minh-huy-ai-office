using System.Data.Common;

namespace MinhHuy.AIOffice.Platform.Persistence;

public interface ISqlConnectionFactory
{
    DbConnection Create(string connectionString);
}
