-- Review of ticket 03: a temporary credential expires. See docs/adr/005-identidade-propria.md and ticket 03 ("Credencial temporaria").
--
-- temporaria_expira_em: until when the temporary credential works. Set when an administrator creates an operational account, cleared when the person
-- changes their password. NULL = not a temporary credential, or a credential created before this migration (which never expires). An expired credential
-- is refused by the application with the same generic failure as a wrong password; only a new reset (ticket 09) brings the account back.
ALTER TABLE credenciais ADD COLUMN temporaria_expira_em timestamptz;
ALTER TABLE credenciais ADD CONSTRAINT credenciais_expira_so_temporaria CHECK (temporaria_expira_em IS NULL OR deve_trocar_senha);

-- The pre-authentication functions return the deadline too. The return type changes, so they are dropped and recreated (same names, same body otherwise,
-- same fixed search_path, same EXECUTE rules as 0007: only flow4tech_app).
DROP FUNCTION identidade_localizar_por_usuario(uuid, text);
DROP FUNCTION identidade_localizar_por_email(text);

CREATE FUNCTION identidade_localizar_por_usuario(p_organizacao uuid, p_usuario text)
RETURNS TABLE (o_conta_id uuid, o_organizacao_id uuid, o_tipo text, o_senha_hash text, o_deve_trocar_senha boolean, o_ativa boolean, o_versao_credencial integer, o_temporaria_expira_em timestamptz)
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
    SELECT c.id, c.organizacao_id, c.tipo, k.senha_hash, k.deve_trocar_senha, c.ativa, c.versao_credencial, k.temporaria_expira_em
      FROM public.contas c
      JOIN public.credenciais k ON k.organizacao_id = c.organizacao_id AND k.conta_id = c.id
     WHERE c.organizacao_id = p_organizacao AND c.tipo = 'operacional' AND c.usuario = p_usuario;
  PERFORM pg_catalog.set_config('app.organizacao_id', '', true);
END
$$;

CREATE FUNCTION identidade_localizar_por_email(p_email text)
RETURNS TABLE (o_conta_id uuid, o_organizacao_id uuid, o_tipo text, o_senha_hash text, o_deve_trocar_senha boolean, o_ativa boolean, o_versao_credencial integer, o_temporaria_expira_em timestamptz)
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
    SELECT c.id, c.organizacao_id, c.tipo, k.senha_hash, k.deve_trocar_senha, c.ativa, c.versao_credencial, k.temporaria_expira_em
      FROM public.contas c
      JOIN public.credenciais k ON k.organizacao_id = c.organizacao_id AND k.conta_id = c.id
     WHERE c.organizacao_id = v_organizacao AND c.id = v_conta AND c.tipo = 'email';
  PERFORM pg_catalog.set_config('app.organizacao_id', '', true);
END
$$;

REVOKE ALL ON FUNCTION identidade_localizar_por_usuario(uuid, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION identidade_localizar_por_email(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION identidade_localizar_por_usuario(uuid, text) TO flow4tech_app;
GRANT EXECUTE ON FUNCTION identidade_localizar_por_email(text) TO flow4tech_app;
