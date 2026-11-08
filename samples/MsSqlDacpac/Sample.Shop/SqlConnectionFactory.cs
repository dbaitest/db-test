using Microsoft.Data.SqlClient;

namespace Sample.Shop;

/// <summary>Opens connections to one SQL Server database.</summary>
public sealed class SqlConnectionFactory(string connectionString) : IShopConnectionFactory
{
    /// <inheritdoc />
    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
