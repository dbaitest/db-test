-- Lists the shop's tables from the catalog views. sys.* comes from the master database model
-- the project references, so this procedure needs master.dacpac at build and deploy time.
CREATE PROCEDURE dbo.ListShopTables
AS
BEGIN
    SET NOCOUNT ON;

    SELECT s.name + N'.' + t.name AS TableName
    FROM sys.tables AS t
    JOIN sys.schemas AS s ON s.schema_id = t.schema_id
    WHERE t.is_ms_shipped = 0
    ORDER BY TableName;
END;
