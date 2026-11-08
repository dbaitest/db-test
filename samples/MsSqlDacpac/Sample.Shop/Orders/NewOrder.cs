namespace Sample.Shop.Orders;

/// <summary>An order to place. The customer is created on its first order.</summary>
public sealed record NewOrder(string Email, string RegionCode, decimal Amount);
