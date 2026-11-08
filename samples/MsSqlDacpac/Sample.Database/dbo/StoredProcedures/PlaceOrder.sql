-- Places an order for a customer, creating the customer on first order. Returns the order id.
CREATE PROCEDURE dbo.PlaceOrder
    @email      NVARCHAR(200),
    @regionCode CHAR(2),
    @amount     DECIMAL(18, 2),
    @orderId    INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @customerId INT = (SELECT Id FROM dbo.Customers WHERE Email = @email);

    IF @customerId IS NULL
    BEGIN
        INSERT INTO dbo.Customers (Email) VALUES (@email);
        SET @customerId = SCOPE_IDENTITY();
    END;

    INSERT INTO dbo.Orders (CustomerId, RegionCode, Amount) VALUES (@customerId, @regionCode, @amount);
    SET @orderId = SCOPE_IDENTITY();
END;
