using Microsoft.Data.SqlClient;

namespace Sample.Shop;

/// <summary>
/// Where the repositories get their connections. An application registers one built from its
/// configuration; a test builds the same one from the test container's connection string.
/// </summary>
public interface IShopConnectionFactory
{
    /// <summary>Creates and opens a connection. The caller disposes it.</summary>
    Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default);
}
