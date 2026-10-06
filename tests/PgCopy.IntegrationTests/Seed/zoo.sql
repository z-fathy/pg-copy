-- "Schema zoo": one of everything the clone has to get right.
CREATE EXTENSION IF NOT EXISTS hstore;
CREATE EXTENSION IF NOT EXISTS citext;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

DO $$ BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'zoo_reader') THEN CREATE ROLE zoo_reader NOLOGIN; END IF;
END $$;
CREATE SCHEMA sales;
CREATE SCHEMA "Odd Schema";

-- Types
CREATE TYPE sales.status AS ENUM ('new', 'paid', 'shipped', 'cancelled');
CREATE TYPE sales.money_pair AS (amount numeric(12,2), currency char(3));
CREATE DOMAIN sales.positive_int AS integer CHECK (VALUE > 0);
CREATE DOMAIN sales.email AS citext;
CREATE TYPE sales.int_range_t AS RANGE (subtype = int4);

-- Plain tables with FKs, generated + identity columns, arrays, jsonb, bytea, quoted names
CREATE TABLE sales.customers (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email       sales.email NOT NULL UNIQUE,
    full_name   text NOT NULL,
    tags        text[] DEFAULT '{}',
    attrs       hstore,
    profile     jsonb,
    created_at  timestamptz NOT NULL DEFAULT now(),
    lower_name  text GENERATED ALWAYS AS (lower(full_name)) STORED
);
CREATE INDEX customers_name_trgm ON sales.customers USING gin (full_name gin_trgm_ops);
CREATE INDEX customers_profile_gin ON sales.customers USING gin (profile);
COMMENT ON TABLE sales.customers IS 'Customer master';
COMMENT ON COLUMN sales.customers.email IS 'Unique login e-mail';

CREATE TABLE sales.orders (
    id          serial PRIMARY KEY,
    customer_id bigint NOT NULL REFERENCES sales.customers(id) ON DELETE CASCADE,
    status      sales.status NOT NULL DEFAULT 'new',
    statuses    sales.status[],
    total       sales.money_pair,
    qty         sales.positive_int,
    window_     sales.int_range_t,
    payload     bytea,
    placed_at   timestamp DEFAULT now()
);
CREATE INDEX orders_customer_idx ON sales.orders (customer_id) WHERE status <> 'cancelled';
CREATE INDEX orders_expr_idx ON sales.orders ((qty * 2), abs(qty));
CREATE INDEX orders_placed_idx ON sales.orders (placed_at);
ALTER TABLE sales.orders CLUSTER ON orders_placed_idx;
ALTER INDEX sales.orders_expr_idx ALTER COLUMN 1 SET STATISTICS 500;  -- stays in post-data

-- Quoted identifiers, dropped column, unlogged table
CREATE TABLE "Odd Schema"."Weird Table ""x""" (
    "Select" int PRIMARY KEY,
    "from" text,
    gone int
);
ALTER TABLE "Odd Schema"."Weird Table ""x""" DROP COLUMN gone;
CREATE UNLOGGED TABLE sales.scratch (k int PRIMARY KEY, v text);

-- Self-referencing + circular deferrable FKs
CREATE TABLE sales.categories (id int PRIMARY KEY, parent_id int REFERENCES sales.categories(id));
CREATE TABLE sales.a (id int PRIMARY KEY, b_id int);
CREATE TABLE sales.b (id int PRIMARY KEY, a_id int REFERENCES sales.a(id) DEFERRABLE INITIALLY DEFERRED);
ALTER TABLE sales.a ADD FOREIGN KEY (b_id) REFERENCES sales.b(id) DEFERRABLE INITIALLY DEFERRED;

-- EXCLUDE constraint
CREATE TABLE sales.reservations (
    id int PRIMARY KEY, room int, during int4range,
    EXCLUDE USING gist (room WITH =, during WITH &&)
);

-- Declarative partitioning (range + list, with partitioned indexes and a default)
CREATE TABLE sales.events (
    id bigint NOT NULL, happened date NOT NULL, kind text NOT NULL, body text,
    PRIMARY KEY (id, happened)
) PARTITION BY RANGE (happened);
CREATE TABLE sales.events_2024 PARTITION OF sales.events FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
CREATE TABLE sales.events_2025 PARTITION OF sales.events FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
CREATE TABLE sales.events_default PARTITION OF sales.events DEFAULT;
CREATE INDEX events_kind_idx ON sales.events (kind);

CREATE TABLE sales.regions (code text, name text) PARTITION BY LIST (code);
CREATE TABLE sales.regions_eu PARTITION OF sales.regions FOR VALUES IN ('DE','FR');
CREATE TABLE sales.regions_other PARTITION OF sales.regions DEFAULT;

-- Legacy inheritance (child rows must not be duplicated into the parent copy)
CREATE TABLE sales.base_log (id int, msg text);
CREATE TABLE sales.child_log (extra text) INHERITS (sales.base_log);

-- Views, matviews, functions, triggers, rules
CREATE VIEW sales.paid_orders AS SELECT * FROM sales.orders WHERE status = 'paid';
CREATE MATERIALIZED VIEW sales.order_totals AS
    SELECT customer_id, count(*) AS n FROM sales.orders GROUP BY customer_id;
CREATE UNIQUE INDEX order_totals_customer ON sales.order_totals (customer_id);

CREATE FUNCTION sales.touch() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN NEW.full_name := trim(NEW.full_name); RETURN NEW; END $$;
CREATE TRIGGER customers_touch BEFORE INSERT OR UPDATE ON sales.customers
    FOR EACH ROW EXECUTE FUNCTION sales.touch();
CREATE FUNCTION sales.order_count(c bigint) RETURNS bigint LANGUAGE sql STABLE AS
    $$ SELECT count(*) FROM sales.orders WHERE customer_id = c $$;
CREATE PROCEDURE sales.noop() LANGUAGE plpgsql AS $$ BEGIN NULL; END $$;

-- Row-level security referencing a role
ALTER TABLE sales.scratch ENABLE ROW LEVEL SECURITY;
CREATE POLICY scratch_read ON sales.scratch FOR SELECT TO zoo_reader USING (true);
CREATE POLICY scratch_all ON sales.scratch USING (true);

-- DB-level setting + sequences with non-trivial state
ALTER DATABASE __DB__ SET search_path = 'public', 'sales';
CREATE SEQUENCE sales.invoice_seq START 1000 INCREMENT 5;

-- ===== Data =====
INSERT INTO sales.customers (email, full_name, tags, attrs, profile)
SELECT 'user' || g || '@example.com', '  Customer ' || g || ' ', ARRAY['t' || (g % 7), 'x'],
       hstore('k', g::text), jsonb_build_object('n', g, 'nested', jsonb_build_object('a', ARRAY[1,2,g]))
FROM generate_series(1, 5000) g;

INSERT INTO sales.orders (customer_id, status, statuses, total, qty, window_, payload)
SELECT 1 + (g % 5000), (ARRAY['new','paid','shipped','cancelled'])[1 + g % 4]::sales.status,
       ARRAY['new','paid']::sales.status[], ROW(g * 1.5, 'EUR')::sales.money_pair, 1 + g % 9,
       sales.int_range_t(g, g + 10), gen_random_bytes(64)
FROM generate_series(1, 20000) g;

INSERT INTO "Odd Schema"."Weird Table ""x""" SELECT g, 'v' || g FROM generate_series(1, 100) g;
INSERT INTO sales.scratch SELECT g, 'u' || g FROM generate_series(1, 50) g;
INSERT INTO sales.categories VALUES (1, NULL), (2, 1), (3, 2);
BEGIN;
SET CONSTRAINTS ALL DEFERRED;
INSERT INTO sales.a VALUES (1, 1), (2, 2);
INSERT INTO sales.b VALUES (1, 1), (2, 2);
COMMIT;
INSERT INTO sales.reservations VALUES (1, 10, '[1,5)'), (2, 10, '[5,9)'), (3, 11, '[1,9)');
INSERT INTO sales.events SELECT g, DATE '2024-01-01' + (g % 900), 'k' || g % 5, repeat('x', 50) FROM generate_series(1, 30000) g;
INSERT INTO sales.regions VALUES ('DE', 'Germany'), ('FR', 'France'), ('US', 'United States');
INSERT INTO sales.base_log VALUES (1, 'base');
INSERT INTO sales.child_log VALUES (2, 'child', 'extra');
SELECT nextval('sales.invoice_seq') FROM generate_series(1, 7);
REFRESH MATERIALIZED VIEW sales.order_totals;

-- Large objects
DO $$
DECLARE oid1 oid;
BEGIN
  oid1 := lo_from_bytea(0, decode(repeat('deadbeef', 1000), 'hex'));
  PERFORM lo_from_bytea(0, 'hello large object'::bytea);
END $$;
ANALYZE;
