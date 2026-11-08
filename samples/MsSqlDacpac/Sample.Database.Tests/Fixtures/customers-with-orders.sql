-- Fixture: two customers and three orders. Invented data, example.test addresses only.
-- No explicit ids: CleanAsync puts every identity back to its seed before each test, so these
-- rows get ids 1 and 2 (customers) and 1, 2 and 3 (orders) on every run.
INSERT INTO dbo.Customers (Email)
VALUES ('ada@example.test'),
       ('grace@example.test');

INSERT INTO dbo.Orders (CustomerId, RegionCode, Amount)
VALUES (1, 'NL', 100.00),
       (2, 'GB', 50.00),
       (1, 'DE', 20.00);
