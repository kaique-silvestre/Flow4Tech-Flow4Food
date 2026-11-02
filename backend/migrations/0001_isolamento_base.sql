-- Tenant isolation foundation. Runs with the OWNER credential (needs CREATEROLE to create the app role).
-- Convention: see "Tenant tables" in CLAUDE.md and docs/adr/002-postgresql-com-rls.md.

-- Application role: no login here (provisioning sets LOGIN + password out of band), never owner, never BYPASSRLS.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'flow4tech_app') THEN
    CREATE ROLE flow4tech_app NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
  END IF;
END
$$;

-- Explicit even though PostgreSQL 15+ already does it: nobody but the owner creates objects in public.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO flow4tech_app;
-- Tables created later by the owner are usable by the app (RLS still filters rows).
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO flow4tech_app;

-- Current organization: set per transaction by the application (set_config(..., true)).
-- After the transaction ends PostgreSQL leaves '' (not NULL) behind, hence NULLIF. No context => NULL => no row matches.
CREATE FUNCTION app_organizacao_id() RETURNS uuid
  LANGUAGE sql STABLE PARALLEL SAFE
  AS $$ SELECT NULLIF(current_setting('app.organizacao_id', true), '')::uuid $$;

CREATE TABLE organizacoes (
  id   uuid PRIMARY KEY,
  nome text NOT NULL
);

CREATE TABLE unidades (
  id             uuid PRIMARY KEY,
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  nome           text NOT NULL,
  UNIQUE (organizacao_id, id)
);

-- Sample business table: copy this shape for every tenant table.
CREATE TABLE amostra_itens (
  id             uuid PRIMARY KEY,
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  unidade_id     uuid,
  nome           text NOT NULL,
  valor_centavos bigint NOT NULL,
  -- Composite FK: a row can only point at a unit of the SAME organization (FK checks bypass RLS).
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id)
);

ALTER TABLE organizacoes   ENABLE ROW LEVEL SECURITY;
ALTER TABLE organizacoes   FORCE  ROW LEVEL SECURITY;
ALTER TABLE unidades       ENABLE ROW LEVEL SECURITY;
ALTER TABLE unidades       FORCE  ROW LEVEL SECURITY;
ALTER TABLE amostra_itens  ENABLE ROW LEVEL SECURITY;
ALTER TABLE amostra_itens  FORCE  ROW LEVEL SECURITY;

-- organizacoes is the tenant root: its key is "id" (the only table without organizacao_id).
CREATE POLICY isolamento ON organizacoes
  USING (id = (SELECT app_organizacao_id()))
  WITH CHECK (id = (SELECT app_organizacao_id()));
CREATE POLICY isolamento ON unidades
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
CREATE POLICY isolamento ON amostra_itens
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
