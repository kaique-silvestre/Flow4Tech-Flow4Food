-- Ticket 09 (plataforma base, spec 5.2.1, 5.2.3, 5.2.5, 5.2.6): invitations, access recovery and the Google link.
--
-- Three tenant tables with the exact RLS policy of amostra_itens. NO new SECURITY DEFINER function and no table without organizacao_id: a token carries its organization as a
-- LOCATOR (`c1.<org>.<secret>`, `a1.<org>.<secret>`, like the refresh token of ticket 04), so the lookup by hash runs UNDER RLS of that organization. Only the SHA-256 of the
-- whole token is stored; the token itself never is. A wrong locator finds nothing.
--
-- convites: an invitation to a person with e-mail. Single use (usado_em), expires (expira_em), can be revoked. The e-mail is reserved on the platform ONLY when the invitation is
-- accepted (the link arrived in that mailbox, which proves possession: the weakness noted in ticket 03). perfil_id / unidade_id say what the new account should receive; they are
-- published in the `identidade.convite-aceito` event and applied by the Permissoes/Organizacao side with the INVITER's rights (not wired yet, see the ticket).
CREATE TABLE convites (
  id             uuid PRIMARY KEY,
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  email          text NOT NULL CHECK (email = lower(email)),
  nome           text NOT NULL CHECK (length(btrim(nome)) BETWEEN 1 AND 200),
  perfil_id      uuid,
  unidade_id     uuid,
  token_hash     bytea NOT NULL CHECK (length(token_hash) = 32),
  criado_por     uuid NOT NULL,
  criado_em      timestamptz NOT NULL,
  expira_em      timestamptz NOT NULL,
  usado_em       timestamptz,
  revogado_em    timestamptz,
  conta_id       uuid,
  UNIQUE (organizacao_id, id),
  UNIQUE (organizacao_id, token_hash),
  CHECK (expira_em > criado_em),
  CHECK (usado_em IS NULL OR revogado_em IS NULL),
  CHECK ((usado_em IS NULL) = (conta_id IS NULL)),
  FOREIGN KEY (organizacao_id, criado_por) REFERENCES contas (organizacao_id, id),
  FOREIGN KEY (organizacao_id, conta_id) REFERENCES contas (organizacao_id, id),
  FOREIGN KEY (organizacao_id, perfil_id) REFERENCES perfis (organizacao_id, id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id)
);

-- One pending invitation per e-mail and organization (a new one revokes the old in the same unit of work).
CREATE UNIQUE INDEX convites_pendente_por_email ON convites (organizacao_id, email) WHERE usado_em IS NULL AND revogado_em IS NULL;

-- recuperacoes: "forgot my password" for accounts with e-mail. Single use, short expiry; a new request supersedes the old (usado_em set).
CREATE TABLE recuperacoes (
  id             uuid PRIMARY KEY,
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  conta_id       uuid NOT NULL,
  token_hash     bytea NOT NULL CHECK (length(token_hash) = 32),
  criado_em      timestamptz NOT NULL,
  expira_em      timestamptz NOT NULL,
  usado_em       timestamptz,
  UNIQUE (organizacao_id, id),
  UNIQUE (organizacao_id, token_hash),
  CHECK (expira_em > criado_em),
  FOREIGN KEY (organizacao_id, conta_id) REFERENCES contas (organizacao_id, id)
);

CREATE INDEX recuperacoes_por_conta ON recuperacoes (organizacao_id, conta_id) WHERE usado_em IS NULL;

-- identidades_externas: the link between an account and an external identity (Google). The account NEVER comes from the provider: a row exists only after the signed-in person
-- confirmed the link (password + same e-mail, verified by the provider). `tipo_conta` plus the composite FK makes it impossible, in the database, to link an operational account
-- (spec 5.2.3 / question 6: operational accounts do not use Google). (provedor, sub) is unique platform-wide: one Google identity, one account.
ALTER TABLE contas ADD CONSTRAINT contas_org_id_tipo_unico UNIQUE (organizacao_id, id, tipo);

CREATE TABLE identidades_externas (
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  conta_id       uuid NOT NULL,
  provedor       text NOT NULL CHECK (provedor IN ('google')),
  sub            text NOT NULL CHECK (length(sub) BETWEEN 1 AND 255),
  email          text NOT NULL CHECK (email = lower(email)),
  tipo_conta     text NOT NULL CHECK (tipo_conta = 'email'),
  criada_em      timestamptz NOT NULL,
  PRIMARY KEY (organizacao_id, conta_id, provedor),
  UNIQUE (provedor, sub),
  FOREIGN KEY (organizacao_id, conta_id, tipo_conta) REFERENCES contas (organizacao_id, id, tipo)
);

ALTER TABLE convites             ENABLE ROW LEVEL SECURITY;
ALTER TABLE convites             FORCE  ROW LEVEL SECURITY;
ALTER TABLE recuperacoes         ENABLE ROW LEVEL SECURITY;
ALTER TABLE recuperacoes         FORCE  ROW LEVEL SECURITY;
ALTER TABLE identidades_externas ENABLE ROW LEVEL SECURITY;
ALTER TABLE identidades_externas FORCE  ROW LEVEL SECURITY;

CREATE POLICY isolamento ON convites
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
CREATE POLICY isolamento ON recuperacoes
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
CREATE POLICY isolamento ON identidades_externas
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
