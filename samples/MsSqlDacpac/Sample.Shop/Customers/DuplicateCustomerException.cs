namespace Sample.Shop.Customers;

/// <summary>A customer with this email address already exists.</summary>
public sealed class DuplicateCustomerException(string email, Exception? innerException)
    : Exception($"A customer with email '{email}' already exists.", innerException)
{
    /// <summary>The email address that is already taken.</summary>
    public string Email { get; } = email;
}
