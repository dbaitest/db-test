-- Reference data: the VAT rate per region. Seeded by Script.PostDeployment.sql.
CREATE TABLE dbo.TaxRates
(
    RegionCode CHAR(2)       NOT NULL CONSTRAINT PK_TaxRates PRIMARY KEY,
    Rate       DECIMAL(5, 4) NOT NULL CONSTRAINT CK_TaxRates_Rate CHECK (Rate >= 0 AND Rate < 1)
);
