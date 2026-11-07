-- Reference data every engine shares, so a test can prove the harness applies a schema
-- and can seed the same rows on all three engines.
IF OBJECT_ID('dbo.customers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.customers (
        id           INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_customers PRIMARY KEY,
        email        NVARCHAR(320) NOT NULL,
        display_name NVARCHAR(200) NOT NULL,
        created_at   DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT uq_customers_email UNIQUE (email)
    );
END;
GO

IF OBJECT_ID('dbo.orders', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.orders (
        id          INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_orders PRIMARY KEY,
        customer_id INT NOT NULL CONSTRAINT fk_orders_customer FOREIGN KEY REFERENCES dbo.customers (id),
        total_cents INT NOT NULL DEFAULT 0,
        placed_at   DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
GO

IF OBJECT_ID('dbo.ix_orders_customer_id', 'IX') IS NULL
BEGIN
    CREATE INDEX ix_orders_customer_id ON dbo.orders (customer_id);
END;
GO
