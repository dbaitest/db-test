using Dapper;

using Microsoft.Data.SqlClient;

namespace Sample.Shop.Customers;

/// <summary>Customers through Dapper. Each method holds one open connection at a time.</summary>
public sealed class DapperCustomerRepository(IShopConnectionFactory connections)
{
    // SQL Server's duplicate-key errors: 2627 for a UNIQUE constraint, 2601 for a unique index.
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    private const string InsertCustomer =
        "INSERT INTO dbo.Customers (Email) OUTPUT INSERTED.Id VALUES (@email)";

    private const string SelectIdByEmail =
        "SELECT Id FROM dbo.Customers WHERE Email = @email";

    private const string SelectTotals =
        """
        SELECT c.Email, t.OrderCount, t.TotalAmount
        FROM dbo.CustomerOrderTotals AS t
        JOIN dbo.Customers AS c ON c.Id = t.CustomerId
        ORDER BY t.TotalAmount DESC, c.Email
        """;

    /// <summary>Adds a customer and returns its id.</summary>
    /// <exception cref="DuplicateCustomerException">The email address is already taken.</exception>
    public async Task<int> AddAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        try
        {
            return await connection.QuerySingleAsync<int>(
                new CommandDefinition(InsertCustomer, new { email }, cancellationToken: cancellationToken));
        }
        catch (SqlException ex) when (ex.Number is UniqueConstraintViolation or UniqueIndexViolation)
        {
            throw new DuplicateCustomerException(email, ex);
        }
    }

    /// <summary>Finds a customer's id by email address, or null when there is none.</summary>
    public async Task<int?> FindIdByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(SelectIdByEmail, new { email }, cancellationToken: cancellationToken));
    }

    /// <summary>Every customer with orders, biggest spender first.</summary>
    public async Task<IReadOnlyList<CustomerTotal>> ListTotalsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var totals = await connection.QueryAsync<CustomerTotal>(
            new CommandDefinition(SelectTotals, cancellationToken: cancellationToken));
        return totals.AsList();
    }
}
