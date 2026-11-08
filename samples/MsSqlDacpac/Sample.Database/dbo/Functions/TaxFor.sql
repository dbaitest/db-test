-- Reads dbo.TaxRates, and dbo.Orders calls it from a computed column: the dependency runs
-- Tables -> Functions -> Tables, so no folder order can apply this project file by file.
CREATE FUNCTION dbo.TaxFor (@regionCode CHAR(2), @amount DECIMAL(18, 2))
RETURNS DECIMAL(18, 2)
WITH SCHEMABINDING
AS
BEGIN
    RETURN
    (
        SELECT CAST(@amount * r.Rate AS DECIMAL(18, 2))
        FROM dbo.TaxRates AS r
        WHERE r.RegionCode = @regionCode
    );
END;
