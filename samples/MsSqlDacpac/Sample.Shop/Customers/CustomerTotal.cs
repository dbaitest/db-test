namespace Sample.Shop.Customers;

/// <summary>What one customer has ordered in total.</summary>
public sealed record CustomerTotal(string Email, long OrderCount, decimal TotalAmount);
