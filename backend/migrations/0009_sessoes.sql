-- Ticket 04 (plataforma base, spec 5.3): sessions and their rotating refresh tokens. See docs/adr/010-sessoes-tokens-e-revogacao.md.
--
-- sessoes: one row per signed-in device. Tenant table with the exact RLS policy; composite FKs make it impossible to point at an account or a unit of ANOTHER
-- organization (FK checks bypass RLS). The session is the authority on every request: the access token only names it (sid) and the server reads this row.
--   * versao_credencial is the account's contas.versao_credencial AT THE MOMENT the session was started. A password change, a deactivation or an owner-flag change
--     bumps the account's value (migration 0007), so every older session stops matching on its next request.
--   * unidade_ativa_id is the unit the session works in (null until the user chooses one when they have several or none). The switch writes it in the same unit of work
--     as its audit record.
--   * expira_inatividade_em slides with activity; expira_em (absolute) never moves. Both come from the host options (IClock), not from the database clock.
--   * familia_id names the refresh chain of the session. A reused refresh revokes the whole family, which is this session (revogada_em + motivo).
--   * ip / agente are truncated by the application (45 / 200) and are for the owner's "where am I signed in" list and the new-login alert (ticket 08).
CREATE TABLE sessoes (
  id                    uuid PRIMARY KEY,
  organizacao_id        uuid NOT NULL REFERENCES organizacoes (id),
  conta_id              uuid NOT NULL,
  unidade_ativa_id      uuid,
  familia_id            uuid NOT NULL,
  deve_trocar_senha     boolean NOT NULL DEFAULT false,
  versao_credencial     integer NOT NULL CHECK (versao_credencial >= 1),
  criada_em             timestamptz NOT NULL,
  ultima_atividade_em   timestamptz NOT NULL,
  expira_inatividade_em timestamptz NOT NULL,
  expira_em             timestamptz NOT NULL,
  revogada_em           timestamptz,
  revogada_motivo       text,
  ip                    text CHECK (length(ip) <= 45),
  agente                text CHECK (length(agente) <= 200),
  UNIQUE (organizacao_id, id),
  CHECK ((revogada_em IS NULL) = (revogada_motivo IS NULL)),
  FOREIGN KEY (organizacao_id, conta_id) REFERENCES contas (organizacao_id, id),
  -- MATCH SIMPLE: a null unit skips the check; a non-null one must be a unit of the SAME organization.
  FOREIGN KEY (organizacao_id, unidade_ativa_id) REFERENCES unidades (organizacao_id, id)
);

CREATE INDEX sessoes_por_conta ON sessoes (organizacao_id, conta_id) WHERE revogada_em IS NULL;

-- sessao_refresh: the refresh tokens of a session, ONE ROW PER TOKEN ISSUED, stored only as SHA-256 (the token itself is never stored). usado_em is set when the token
-- is exchanged for the next one; presenting a token whose usado_em is set is a reuse (theft or a replay) and revokes the session. Rows used more than a day ago are
-- pruned at each rotation.
CREATE TABLE sessao_refresh (
  id             uuid PRIMARY KEY,
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  sessao_id      uuid NOT NULL,
  hash           bytea NOT NULL CHECK (length(hash) = 32),
  criado_em      timestamptz NOT NULL,
  usado_em       timestamptz,
  UNIQUE (organizacao_id, hash),
  FOREIGN KEY (organizacao_id, sessao_id) REFERENCES sessoes (organizacao_id, id) ON DELETE CASCADE
);

CREATE INDEX sessao_refresh_por_sessao ON sessao_refresh (organizacao_id, sessao_id);

ALTER TABLE sessoes        ENABLE ROW LEVEL SECURITY;
ALTER TABLE sessoes        FORCE  ROW LEVEL SECURITY;
ALTER TABLE sessao_refresh ENABLE ROW LEVEL SECURITY;
ALTER TABLE sessao_refresh FORCE  ROW LEVEL SECURITY;

CREATE POLICY isolamento ON sessoes
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
CREATE POLICY isolamento ON sessao_refresh
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

-- Removing a user's link to a unit ends the sessions that are working in that unit (spec 5.3.5), in the same transaction as the removal. The trigger runs with the
-- rights of the caller, so RLS applies (same organization context). It is the only time a clock of the database is used for a session: the removal is not an
-- application event with an IClock at hand, and revogada_em is informational (the session is invalid as soon as the row says revoked).
CREATE FUNCTION sessoes_revogar_por_vinculo_removido() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
  UPDATE public.sessoes
     SET revogada_em = pg_catalog.clock_timestamp(), revogada_motivo = 'vinculo-removido'
   WHERE organizacao_id = OLD.organizacao_id
     AND conta_id = OLD.usuario_id
     AND unidade_ativa_id = OLD.unidade_id
     AND revogada_em IS NULL;
  RETURN OLD;
END;
$$;

CREATE TRIGGER usuario_unidades_revoga_sessoes
  AFTER DELETE ON usuario_unidades
  FOR EACH ROW EXECUTE FUNCTION sessoes_revogar_por_vinculo_removido();
