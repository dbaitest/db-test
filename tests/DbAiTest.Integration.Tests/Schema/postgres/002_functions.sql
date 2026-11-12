-- A dollar-quoted function body. The statement splitter must not break the inner
-- semicolons, and the file must apply as a single statement.
CREATE OR REPLACE FUNCTION customer_order_total(p_customer_id INTEGER)
RETURNS BIGINT
LANGUAGE plpgsql
AS $$
DECLARE
    v_total BIGINT;
BEGIN
    SELECT COALESCE(SUM(total_cents), 0)
      INTO v_total
      FROM orders
     WHERE customer_id = p_customer_id;

    RETURN v_total;
END;
$$;
