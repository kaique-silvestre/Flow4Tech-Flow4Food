-- Ticket 02 (plataforma base, spec 5.6): immutable audit trail, hash-chained per organization. Tenant table, same RLS shape as amostra_itens.
--
-- Chain: each record stores the hash of the previous record OF THE SAME ORGANIZATION (hash_anterior; NULL only for sequencia = 1) and its own hash
-- (SHA-256 over a canonical form of every other column, computed by the application; see AuditHash in Flow4Tech.Infrastructure.Audit).
-- (organizacao_id, sequencia) is unique: even without the application's per-organization lock, a fork cannot be stored.
-- usuario_id / sessao_id have no foreign key: the identity tables arrive with tickets 03/04, which may add them in their own migration.
-- usuario_id is NOT a foreign key on purpose for the long run too: an audit record must outlive the user it names.
CREATE TABLE auditoria_registros (
  id             uuid PRIMARY KEY,
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  unidade_id     uuid,
  usuario_id     uuid,
  sessao_id      uuid,
  acao           text NOT NULL CHECK (acao ~ '^[a-z][a-z0-9-]*:[a-z][a-z0-9_-]*$'),
  objeto_tipo    text,
  objeto_id      text,
  antes          jsonb,
  depois         jsonb,
  resultado      text NOT NULL CHECK (resultado IN ('sucesso', 'negado', 'falha')),
  correlation_id text NOT NULL,
  ocorrido_em    timestamptz NOT NULL,
  sequencia      bigint NOT NULL CHECK (sequencia >= 1),
  hash_anterior  bytea CHECK (octet_length(hash_anterior) = 32),
  hash           bytea NOT NULL CHECK (octet_length(hash) = 32),
  UNIQUE (organizacao_id, sequencia),
  UNIQUE (organizacao_id, id),
  CHECK ((sequencia = 1) = (hash_anterior IS NULL)),
  -- Composite FK: a record can only point at a unit of the SAME organization (FK checks bypass RLS).
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id)
);

ALTER TABLE auditoria_registros ENABLE ROW LEVEL SECURITY;
ALTER TABLE auditoria_registros FORCE  ROW LEVEL SECURITY;

-- Exactly the isolation policy every tenant table has (RlsArchitectureIntegrationTests accepts nothing else): append-only is enforced by PRIVILEGE below.
CREATE POLICY isolamento ON auditoria_registros
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

-- Append-only for the application credential. Default privileges (0001) grant SELECT, INSERT, UPDATE, DELETE on every new table; take the rest back.
-- The application can read and append, nothing else: UPDATE / DELETE / TRUNCATE fail with "permission denied" (SQLSTATE 42501).
REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON auditoria_registros FROM flow4tech_app;
REVOKE ALL ON auditoria_registros FROM PUBLIC;
