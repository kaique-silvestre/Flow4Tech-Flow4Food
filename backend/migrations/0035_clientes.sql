-- Clientes (F4-0147, ADR-017): the customers of an organization, with the LGPD consent, and the optional link of a customer to a comanda.
--   clientes            one row per customer. telefone/email/documento are stored NORMALIZED (phone and document as digits, e-mail in lower case) so the duplicate check is a plain equality.
--                       The consent is part of the row (granted, when, purpose code, text version, who registered, revoked at); registering needs it granted. estado is 'ativo' or 'anonimizado':
--                       anonymizing ("forgetting") clears the name and the three contacts but keeps the row, so the id and the financial history that points to it stay. The app credential has no
--                       DELETE: a customer is never removed, only anonymized.
--   comandas.cliente_id optional link; the composite FK refuses a customer of another organization, and RLS hides it from the application anyway.
CREATE TABLE clientes (
  id                          uuid PRIMARY KEY,
  organizacao_id              uuid        NOT NULL REFERENCES organizacoes (id),
  nome                        text        NOT NULL CHECK (length(btrim(nome)) BETWEEN 1 AND 120),
  telefone                    text        CHECK (telefone ~ '^[0-9]{10,11}$'),
  email                       text        CHECK (length(email) BETWEEN 3 AND 254),
  documento                   text        CHECK (documento ~ '^([0-9]{11}|[0-9]{14})$'),
  consentimento_concedido     boolean     NOT NULL,
  consentimento_em            timestamptz,
  consentimento_finalidade    text,
  consentimento_versao        text,
  consentimento_por           uuid,
  consentimento_revogado_em   timestamptz,
  estado                      text        NOT NULL DEFAULT 'ativo' CHECK (estado IN ('ativo', 'anonimizado')),
  criado_em                   timestamptz NOT NULL,
  anonimizado_em              timestamptz,
  UNIQUE (organizacao_id, id),
  FOREIGN KEY (organizacao_id, consentimento_por) REFERENCES contas (organizacao_id, id),
  -- An anonymized customer holds no personal data.
  CONSTRAINT clientes_anonimizado_sem_dados CHECK (estado = 'ativo' OR (telefone IS NULL AND email IS NULL AND documento IS NULL AND anonimizado_em IS NOT NULL))
);

CREATE INDEX clientes_por_telefone ON clientes (organizacao_id, telefone) WHERE telefone IS NOT NULL;
CREATE INDEX clientes_por_email ON clientes (organizacao_id, email) WHERE email IS NOT NULL;
CREATE INDEX clientes_por_documento ON clientes (organizacao_id, documento) WHERE documento IS NOT NULL;
CREATE INDEX clientes_por_nome ON clientes (organizacao_id, lower(nome));

ALTER TABLE clientes ENABLE ROW LEVEL SECURITY;
ALTER TABLE clientes FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON clientes
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

REVOKE DELETE, TRUNCATE, REFERENCES, TRIGGER ON clientes FROM flow4tech_app;
REVOKE ALL ON clientes FROM PUBLIC;

ALTER TABLE comandas ADD COLUMN cliente_id uuid;
ALTER TABLE comandas ADD FOREIGN KEY (organizacao_id, cliente_id) REFERENCES clientes (organizacao_id, id);
CREATE INDEX comandas_por_cliente ON comandas (organizacao_id, cliente_id) WHERE cliente_id IS NOT NULL;
