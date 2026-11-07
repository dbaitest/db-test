-- A stored procedure whose body contains semicolons. The DELIMITER directive must be
-- honoured so the body arrives at the server intact.
DELIMITER //

CREATE PROCEDURE customer_order_total(IN p_customer_id INT, OUT p_total BIGINT)
BEGIN
    DECLARE v_total BIGINT;

    SELECT COALESCE(SUM(total_cents), 0)
      INTO v_total
      FROM orders
     WHERE customer_id = p_customer_id;

    SET p_total = v_total;
END //

DELIMITER ;
