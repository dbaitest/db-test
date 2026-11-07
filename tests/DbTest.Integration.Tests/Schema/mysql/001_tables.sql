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
