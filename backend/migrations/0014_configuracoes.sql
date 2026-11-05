-- Ticket 10 (plataforma base, spec 5.7): layered settings. One row is one VERSION of an option at one scope (the organization when unidade_id IS NULL, otherwise one unit).
-- The options themselves (key, type, default, layers) are declared in code; this table keeps only what an organization or a unit chose, and its whole history.
--
-- Append-only by PRIVILEGE (like the audit trail): a change is a new version, a restore is a new version that copies an old one, "inherit again" is a version with valor NULL.
-- Nothing is updated or deleted, so "who changed what, when, from which value" is always readable (5.7.3).
--   valor                NULL = inherit from the layer above.
--   travado              organization scope only: the units may not override while the effective version of the organization is locked (5.7.1).
--   vigente_a_partir_de  the instant the version starts to apply. CHECK: never before criado_em, so a change cannot reach back (5.7.4).
--   versao               1, 2, 3... per (organization, scope, key); the highest version in force wins, so a version written later replaces one that was scheduled and has not started.
--   restaura_versao      the version a restore copied (history only).
-- criado_por is a composite FK to contas (accounts are deactivated, never deleted). Tenant table with the exact RLS policy.
CREATE TABLE configuracoes_versoes (
  id                  uuid PRIMARY KEY,
  organizacao_id      uuid NOT NULL REFERENCES organizacoes (id),
  unidade_id          uuid,
  chave               text NOT NULL CHECK (chave ~ '^[a-z][a-z0-9-]*\.[a-z][a-z0-9-]*$' AND length(chave) <= 80),
  versao              integer NOT NULL CHECK (versao >= 1),
  valor               text CHECK (valor IS NULL OR length(valor) BETWEEN 1 AND 200),
  travado             boolean NOT NULL DEFAULT false,
  vigente_a_partir_de timestamptz NOT NULL,
  criado_em           timestamptz NOT NULL,
  criado_por          uuid,
  motivo              text CHECK (motivo IS NULL OR length(motivo) <= 500),
  restaura_versao     integer CHECK (restaura_versao IS NULL OR restaura_versao >= 1),
  UNIQUE (organizacao_id, id),
  CHECK (vigente_a_partir_de >= criado_em),
  CHECK (unidade_id IS NULL OR NOT travado),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, criado_por) REFERENCES contas (organizacao_id, id)
);

-- One version number per scope and key (NULLS NOT DISTINCT: the organization scope has no unit and must be unique too). It is also the backstop of the writer's advisory lock.
CREATE UNIQUE INDEX configuracoes_versoes_escopo ON configuracoes_versoes (organizacao_id, unidade_id, chave, versao) NULLS NOT DISTINCT;
CREATE INDEX configuracoes_versoes_por_chave ON configuracoes_versoes (organizacao_id, chave, vigente_a_partir_de);

ALTER TABLE configuracoes_versoes ENABLE ROW LEVEL SECURITY;
ALTER TABLE configuracoes_versoes FORCE  ROW LEVEL SECURITY;

CREATE POLICY isolamento ON configuracoes_versoes
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

-- Default privileges (0001) grant SELECT, INSERT, UPDATE, DELETE on every new table; take the rest back. The application reads and appends, nothing else (SQLSTATE 42501).
REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON configuracoes_versoes FROM flow4tech_app;
REVOKE ALL ON configuracoes_versoes FROM PUBLIC;
