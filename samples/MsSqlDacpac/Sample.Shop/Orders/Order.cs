namespace Sample.Shop.Orders;

/// <summary>An order as stored. <see cref="Tax"/> is null when the region has no tax rate.</summary>
public sealed record Order(int Id, int CustomerId, string RegionCode, decimal Amount, decimal? Tax);
