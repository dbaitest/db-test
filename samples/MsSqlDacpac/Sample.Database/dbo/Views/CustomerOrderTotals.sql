CREATE VIEW dbo.CustomerOrderTotals
WITH SCHEMABINDING
AS
SELECT
    o.CustomerId,
    COUNT_BIG(*)  AS OrderCount,
    SUM(o.Amount) AS TotalAmount
FROM dbo.Orders AS o
GROUP BY o.CustomerId;
