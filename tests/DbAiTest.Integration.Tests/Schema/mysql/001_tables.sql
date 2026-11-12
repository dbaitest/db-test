-- Reference data every engine shares, so a test can prove the harness applies a schema
-- and can seed the same rows on all three engines.
CREATE TABLE IF NOT EXISTS customers (
    id           INT NOT NULL AUTO_INCREMENT,
    email        VARCHAR(320) NOT NULL,
    display_name VARCHAR(200) NOT NULL,
    created_at   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (id),
    UNIQUE KEY uq_customers_email (email)
);

CREATE TABLE IF NOT EXISTS orders (
    id          INT NOT NULL AUTO_INCREMENT,
    customer_id INT NOT NULL,
    total_cents INT NOT NULL DEFAULT 0,
    placed_at   DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (id),
    KEY ix_orders_customer_id (customer_id),
    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customers (id)
);

-- A table no test seeds, so the first row it ever receives proves that Clean reset the
-- identity of a table that never held a row: SQL Server hands such a table the reseed
-- value itself, so a reseed to 0 made its first id 0 instead of 1.
CREATE TABLE IF NOT EXISTS audit_entries (
    id   INT NOT NULL AUTO_INCREMENT,
    note VARCHAR(200) NOT NULL,
    PRIMARY KEY (id)
);

-- References audit_entries, so Clean must empty it despite the foreign key.
CREATE TABLE IF NOT EXISTS audit_entry_tags (
    audit_entry_id INT NOT NULL,
    tag            VARCHAR(50) NOT NULL,
    CONSTRAINT fk_audit_entry_tags_entry FOREIGN KEY (audit_entry_id) REFERENCES audit_entries (id)
);
