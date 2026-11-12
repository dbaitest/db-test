-- A stored procedure in its own batch, separated by GO. SQL Server rejects a GO on the wire,
-- and CREATE OR ALTER PROCEDURE must be the first statement of its batch, so the harness has
-- to split this file on GO before applying it.
CREATE OR ALTER PROCEDURE dbo.customer_order_total
    @customer_id INT,
    @total BIGINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT @total = COALESCE(SUM(total_cents), 0)
    FROM dbo.orders
    WHERE customer_id = @customer_id;
END
GO
