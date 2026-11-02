-- Ticket 03 (plataforma base, spec 5.2): accounts, credentials and the pre-authentication lookup. See docs/adr/005-identidade-propria.md.
--
-- Premise (README, question 1 of the spec): ONE account belongs to ONE organization. An e-mail is therefore unique across the platform, an operational user
-- name is unique inside its organization only.
--
-- contas / credenciais: tenant tables with the exact RLS policy of amostra_itens. The password hash lives ONLY in credenciais.
-- versao_credencial (contas) grows on every event that must end the account's sessions (password change, deactivation, owner flag change): ticket 04 revokes
-- the sessions that carry an older value. Deactivating an account never deletes anything (ativa = false, desativada_em set).
CREATE TABLE contas (
  id                uuid PRIMARY KEY,
  organizacao_id    uuid NOT NULL REFERENCES organizacoes (id),
  tipo              text NOT NULL CHECK (tipo IN ('email', 'operacional')),
  nome              text NOT NULL,
  email             text,
  usuario           text,
  dono              boolean NOT NULL DEFAULT false,
  ativa             boolean NOT NULL DEFAULT true,
  versao_credencial integer NOT NULL DEFAULT 1 CHECK (versao_credencial >= 1),
  criada_em         timestamptz NOT NULL,
  desativada_em     timestamptz,
  UNIQUE (organizacao_id, id),
  -- An account with e-mail has an e-mail and no user name; an operational account the reverse (no e-mail, spec 5.2.1).
  CHECK ((tipo = 'email') = (email IS NOT NULL)),
  CHECK ((tipo = 'operacional') = (usuario IS NOT NULL)),
  -- Only accounts with e-mail can be owners; the owner is the person who signed up.
  CHECK (NOT dono OR tipo = 'email'),
  CHECK (ativa = (desativada_em IS NULL)),
  -- The login looks accounts up by the EXACT normalized key (lower case): a row stored in another case could never be found, so the database refuses it.
  CHECK (email = lower(email)),
  CHECK (usuario = lower(usuario))
);

-- The user name is unique INSIDE the organization (two organizations may both have "maria").
CREATE UNIQUE INDEX contas_usuario_por_organizacao ON contas (organizacao_id, usuario) WHERE usuario IS NOT NULL;
CREATE UNIQUE INDEX contas_email_por_organizacao ON contas (organizacao_id, email) WHERE email IS NOT NULL;

CREATE TABLE credenciais (
  organizacao_id    uuid NOT NULL REFERENCES organizacoes (id),
  conta_id          uuid NOT NULL,
  senha_hash        text NOT NULL,
  deve_trocar_senha boolean NOT NULL,
  atualizada_em     timestamptz NOT NULL,
  PRIMARY KEY (organizacao_id, conta_id),
  -- Composite FK: a credential can only belong to an account of the SAME organization (FK checks bypass RLS).
  FOREIGN KEY (organizacao_id, conta_id) REFERENCES contas (organizacao_id, id)
);

ALTER TABLE contas      ENABLE ROW LEVEL SECURITY;
ALTER TABLE contas      FORCE  ROW LEVEL SECURITY;
ALTER TABLE credenciais ENABLE ROW LEVEL SECURITY;
ALTER TABLE credenciais FORCE  ROW LEVEL SECURITY;

CREATE POLICY isolamento ON contas
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
CREATE POLICY isolamento ON credenciais
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

-- Ticket 01 left usuario_unidades.usuario_id without a foreign key "until the identity tables exist": now it points at the account of the same organization.
-- (Composite FK, like every other tenant reference.) Migrations run on empty or development databases at this point; there are no production links to backfill.
ALTER TABLE usuario_unidades
  ADD CONSTRAINT usuario_unidades_conta_fk FOREIGN KEY (organizacao_id, usuario_id) REFERENCES contas (organizacao_id, id);

-- ---------------------------------------------------------------------------------------------------------------------------------------------------
-- The ONE table without organizacao_id (listed as an exception in RlsArchitectureIntegrationTests): e-mail -> (organization, account).
-- The login receives only an e-mail, so it must find the organization before any context exists. No RLS (no tenant owns the row) and NO privilege for
-- the application role: it is read and written only by the SECURITY DEFINER functions below, which return one exact match, never a list.
-- It also makes "one e-mail, one account, one organization" a platform-wide constraint (the PRIMARY KEY).
CREATE TABLE identidade_emails (
  email          text PRIMARY KEY,
  organizacao_id uuid NOT NULL,
  conta_id       uuid NOT NULL,
  UNIQUE (organizacao_id, conta_id),
  FOREIGN KEY (organizacao_id, conta_id) REFERENCES contas (organizacao_id, id)
);
-- 0001 grants the application role DML on every new table by default: take it all back.
REVOKE ALL ON identidade_emails FROM PUBLIC;
REVOKE ALL ON identidade_emails FROM flow4tech_app;

-- ---------------------------------------------------------------------------------------------------------------------------------------------------
-- Pre-authentication lookup (spec 5.2.4). Owned by the migration (owner) role, which is subject to FORCE ROW LEVEL SECURITY like everyone else and has no
-- BYPASSRLS: the function works UNDER RLS by setting the transaction-local organization context to the organization it was asked about (exact key), reading
-- the one account, and restoring whatever context was there. So no role that ignores RLS exists or is needed.
--   * search_path is fixed (pg_catalog, pg_temp) and every object is schema-qualified: nobody can shadow a name.
--   * EXECUTE only for flow4tech_app (PUBLIC revoked).
--   * Exact key in, at most ONE row out; no pattern, no list, no count. A deactivated account is still returned (the caller does the same hashing work and
--     answers the same generic failure).
--   * It refuses to run inside an organization context: the login runs it with none, so a statement injected into a tenant transaction cannot use it.
CREATE FUNCTION identidade_localizar_por_usuario(p_organizacao uuid, p_usuario text)
RETURNS TABLE (o_conta_id uuid, o_organizacao_id uuid, o_tipo text, o_senha_hash text, o_deve_trocar_senha boolean, o_ativa boolean, o_versao_credencial integer)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
  IF public.app_organizacao_id() IS NOT NULL THEN
    RAISE EXCEPTION 'pre-autenticacao nao roda dentro de um contexto de organizacao' USING ERRCODE = '42501';
  END IF;
  IF p_organizacao IS NULL OR p_usuario IS NULL OR length(p_usuario) > 64 THEN
    RETURN;
  END IF;
  PERFORM pg_catalog.set_config('app.organizacao_id', p_organizacao::text, true);
  RETURN QUERY
    SELECT c.id, c.organizacao_id, c.tipo, k.senha_hash, k.deve_trocar_senha, c.ativa, c.versao_credencial
      FROM public.contas c
      JOIN public.credenciais k ON k.organizacao_id = c.organizacao_id AND k.conta_id = c.id
     WHERE c.organizacao_id = p_organizacao AND c.tipo = 'operacional' AND c.usuario = p_usuario;
  PERFORM pg_catalog.set_config('app.organizacao_id', '', true);
END
$$;

CREATE FUNCTION identidade_localizar_por_email(p_email text)
RETURNS TABLE (o_conta_id uuid, o_organizacao_id uuid, o_tipo text, o_senha_hash text, o_deve_trocar_senha boolean, o_ativa boolean, o_versao_credencial integer)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
  v_organizacao uuid;
  v_conta uuid;
BEGIN
  IF public.app_organizacao_id() IS NOT NULL THEN
    RAISE EXCEPTION 'pre-autenticacao nao roda dentro de um contexto de organizacao' USING ERRCODE = '42501';
  END IF;
  IF p_email IS NULL OR length(p_email) > 254 THEN
    RETURN;
  END IF;
  SELECT e.organizacao_id, e.conta_id INTO v_organizacao, v_conta FROM public.identidade_emails e WHERE e.email = p_email;
  IF NOT FOUND THEN
    RETURN;
  END IF;
  PERFORM pg_catalog.set_config('app.organizacao_id', v_organizacao::text, true);
  RETURN QUERY
    SELECT c.id, c.organizacao_id, c.tipo, k.senha_hash, k.deve_trocar_senha, c.ativa, c.versao_credencial
      FROM public.contas c
      JOIN public.credenciais k ON k.organizacao_id = c.organizacao_id AND k.conta_id = c.id
     WHERE c.organizacao_id = v_organizacao AND c.id = v_conta AND c.tipo = 'email';
  PERFORM pg_catalog.set_config('app.organizacao_id', '', true);
END
$$;

-- Registers the e-mail of an account of the CALLER'S OWN organization (the organization context must be set and the account must be visible under it with
-- exactly that e-mail), in the same transaction as the account insert. False when the e-mail already belongs to another account (any organization).
CREATE FUNCTION identidade_registrar_email(p_conta uuid, p_email text)
RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
  v_organizacao uuid;
  v_linhas integer;
BEGIN
  v_organizacao := public.app_organizacao_id();
  IF v_organizacao IS NULL THEN
    RAISE EXCEPTION 'sem contexto de organizacao' USING ERRCODE = '42501';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM public.contas c WHERE c.id = p_conta AND c.organizacao_id = v_organizacao AND c.email = p_email) THEN
    RAISE EXCEPTION 'conta nao encontrada nesta organizacao' USING ERRCODE = '42501';
  END IF;
  INSERT INTO public.identidade_emails (email, organizacao_id, conta_id) VALUES (p_email, v_organizacao, p_conta) ON CONFLICT (email) DO NOTHING;
  GET DIAGNOSTICS v_linhas = ROW_COUNT;
  RETURN v_linhas = 1;
END
$$;

REVOKE ALL ON FUNCTION identidade_localizar_por_usuario(uuid, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION identidade_localizar_por_email(text) FROM PUBLIC;
REVOKE ALL ON FUNCTION identidade_registrar_email(uuid, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION identidade_localizar_por_usuario(uuid, text) TO flow4tech_app;
GRANT EXECUTE ON FUNCTION identidade_localizar_por_email(text) TO flow4tech_app;
GRANT EXECUTE ON FUNCTION identidade_registrar_email(uuid, text) TO flow4tech_app;
