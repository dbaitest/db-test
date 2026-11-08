using System.Data;

using Microsoft.Data.SqlClient;

namespace Sample.Shop.Orders;

/// <summary>
/// Orders through plain ADO.NET. Each method holds one open connection at a time, so a caller's
/// ambient <c>TransactionScope</c> stays a local transaction instead of being promoted to a
/// distributed one.
/// </summary>
public sealed class SqlOrderRepository(IShopConnectionFactory connections)
{
    private const string SelectOrder =
        "SELECT Id, CustomerId, RegionCode, Amount, Tax FROM dbo.Orders WHERE Id = @id";

    /// <summary>Places one order through <c>dbo.PlaceOrder</c> and returns its id.</summary>
    public async Task<int> PlaceAsync(NewOrder order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        await using var connection = await connections.OpenAsync(cancellationToken);
        return await PlaceAsync(connection, transaction: null, order, cancellationToken);
    }

    /// <summary>
    /// Places every order in one transaction and returns their ids in order. When any order
    /// fails, none of them — and none of the customers created for them — is kept.
    /// </summary>
    public async Task<IReadOnlyList<int>> PlaceAllAsync(
        IReadOnlyList<NewOrder> orders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orders);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var ids = new List<int>(orders.Count);
        foreach (var order in orders)
        {
            ArgumentNullException.ThrowIfNull(order);
            ids.Add(await PlaceAsync(connection, transaction, order, cancellationToken));
        }

        // Disposing an uncommitted transaction rolls it back, so an exception above undoes it all.
        await transaction.CommitAsync(cancellationToken);
        return ids;
    }

    /// <summary>Reads an order, or null when there is none with that id.</summary>
    public async Task<Order?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(SelectOrder, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Order(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetDecimal(3),
            await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetDecimal(4));
    }

    private static async Task<int> PlaceAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        NewOrder order,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("dbo.PlaceOrder", connection, transaction)
        {
            CommandType = CommandType.StoredProcedure,
        };

        command.Parameters.Add("@email", SqlDbType.NVarChar, 200).Value = order.Email;
        command.Parameters.Add("@regionCode", SqlDbType.Char, 2).Value = order.RegionCode;
        var amount = command.Parameters.Add("@amount", SqlDbType.Decimal);
        amount.Precision = 18;
        amount.Scale = 2;
        amount.Value = order.Amount;
        var orderId = command.Parameters.Add("@orderId", SqlDbType.Int);
        orderId.Direction = ParameterDirection.Output;

        await command.ExecuteNonQueryAsync(cancellationToken);
        return (int)orderId.Value;
    }
}
