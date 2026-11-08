CREATE TABLE dbo.Orders
(
    Id         INT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
    CustomerId INT                NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers (Id),
    RegionCode CHAR(2)            NOT NULL,
    Amount     DECIMAL(18, 2)     NOT NULL CONSTRAINT CK_Orders_Amount CHECK (Amount > 0),
    -- Computed through a function that reads another table.
    Tax        AS dbo.TaxFor(RegionCode, Amount)
);
