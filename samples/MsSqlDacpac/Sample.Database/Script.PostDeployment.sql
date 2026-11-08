-- Post-deployment script: seeds the reference data. Idempotent, so it can run after every
-- deploy, and free of SQLCMD syntax, so the tests can run it again after CleanAsync() has
-- emptied every table.
MERGE dbo.TaxRates AS target
USING (VALUES ('NL', 0.2100), ('DE', 0.1900), ('GB', 0.2000)) AS source (RegionCode, Rate)
    ON target.RegionCode = source.RegionCode
WHEN MATCHED AND target.Rate <> source.Rate THEN
    UPDATE SET Rate = source.Rate
WHEN NOT MATCHED THEN
    INSERT (RegionCode, Rate) VALUES (source.RegionCode, source.Rate);
