-- Ticket 08 (plataforma base, spec 5.5.5): the devices and networks already seen for an account, for the new-login alert.
-- One row per (account, kind, value): kind 'aparelho' = the user agent with its digits removed, 'rede' = the network prefix of the address (IPv4 /24, IPv6 /64). Values are
-- normalized and truncated by the application (ImpressaoDeAcesso) and held in clear text, like the address and agent already stored with each session. The application keeps
-- at most 50 per account and kind (oldest dropped on insert), so the table cannot grow without bound. Tenant table with the exact RLS policy; the composite FK ties a row to an
-- account of the SAME organization.
CREATE TABLE acessos_conhecidos (
  id                uuid PRIMARY KEY,
  organizacao_id    uuid NOT NULL REFERENCES organizacoes (id),
  conta_id          uuid NOT NULL,
  tipo              text NOT NULL CHECK (tipo IN ('aparelho', 'rede')),
  valor             text NOT NULL CHECK (length(valor) BETWEEN 1 AND 200),
  visto_primeiro_em timestamptz NOT NULL,
  visto_ultimo_em   timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  UNIQUE (organizacao_id, conta_id, tipo, valor),
  FOREIGN KEY (organizacao_id, conta_id) REFERENCES contas (organizacao_id, id)
);

CREATE INDEX acessos_conhecidos_por_conta ON acessos_conhecidos (organizacao_id, conta_id, tipo, visto_ultimo_em DESC);

ALTER TABLE acessos_conhecidos ENABLE ROW LEVEL SECURITY;
ALTER TABLE acessos_conhecidos FORCE  ROW LEVEL SECURITY;

CREATE POLICY isolamento ON acessos_conhecidos
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
