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

-- A table no test seeds, so the first row it ever receives proves that Clean reset the
-- identity of a table that never held a row: SQL Server hands such a table the reseed
-- value itself, so a reseed to 0 made its first id 0 instead of 1.
IF OBJECT_ID('dbo.audit_entries', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.audit_entries (
        id   INT IDENTITY(1,1) NOT NULL CONSTRAINT pk_audit_entries PRIMARY KEY,
        note NVARCHAR(200) NOT NULL
    );
END;
GO

-- References audit_entries, so Clean must DELETE it and reseed rather than TRUNCATE it.
IF OBJECT_ID('dbo.audit_entry_tags', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.audit_entry_tags (
        audit_entry_id INT NOT NULL CONSTRAINT fk_audit_entry_tags_entry FOREIGN KEY REFERENCES dbo.audit_entries (id),
        tag            NVARCHAR(50) NOT NULL
    );
END;
GO
