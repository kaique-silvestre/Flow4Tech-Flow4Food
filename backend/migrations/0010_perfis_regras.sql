-- Ticket 06 (plataforma base, spec 5.4.3 to 5.4.9): profiles, rules, individual overrides and profile assignments, persisted per organization.
--
-- The FACTORY profiles (dono, conta) are not rows: they are code (PoliticaDeFabrica), so they cannot be edited through the database by construction and a catalog rename
-- cannot leave stale factory rows behind. Everything below is organization data and has the exact RLS policy of amostra_itens.
--   * perfis: a custom profile. `modelo` names the template it was made from (a factory profile name or the id of another custom profile, as text); with
--     acompanha_modelo = true the profile FOLLOWS the template (its rules are the template's plus its own), otherwise the rules were COPIED once at creation.
--   * perfil_regras: the rules of a custom profile (effect, permission, optional unit, limits). Limits only on allow rules (as in the PolicySet).
--   * usuario_perfis: which custom profiles an account holds. RESTRICT on the profile: a held profile cannot be deleted, the assignments go first.
--   * usuario_regras: individual overrides, same shape as perfil_regras but attached to one account.
-- Every change to who may do what ends the affected sessions (spec 5.4.9): triggers bump contas.versao_credencial, the same lever a password change uses, so every older
-- session of that account stops matching on its next request. The triggers run with the rights of the caller, so RLS applies (same organization context).
CREATE TABLE perfis (
  id               uuid PRIMARY KEY,
  organizacao_id   uuid NOT NULL REFERENCES organizacoes (id),
  nome             text NOT NULL CHECK (length(btrim(nome)) BETWEEN 1 AND 80),
  modelo           text CHECK (length(modelo) <= 80),
  acompanha_modelo boolean NOT NULL DEFAULT false,
  criado_em        timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  UNIQUE (organizacao_id, nome),
  CHECK (NOT acompanha_modelo OR modelo IS NOT NULL)
);

CREATE TABLE perfil_regras (
  id                   uuid PRIMARY KEY,
  organizacao_id       uuid NOT NULL REFERENCES organizacoes (id),
  perfil_id            uuid NOT NULL,
  permissao            text NOT NULL CHECK (length(permissao) BETWEEN 3 AND 120),
  efeito               text NOT NULL CHECK (efeito IN ('allow', 'deny')),
  unidade_id           uuid,
  max_percentual       numeric(5, 2) CHECK (max_percentual BETWEEN 0 AND 100),
  max_valor_centavos   bigint CHECK (max_valor_centavos >= 0),
  teto_diario_centavos bigint CHECK (teto_diario_centavos >= 0),
  CHECK (efeito = 'allow' OR (max_percentual IS NULL AND max_valor_centavos IS NULL AND teto_diario_centavos IS NULL)),
  FOREIGN KEY (organizacao_id, perfil_id) REFERENCES perfis (organizacao_id, id) ON DELETE CASCADE,
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id)
);

CREATE INDEX perfil_regras_por_perfil ON perfil_regras (organizacao_id, perfil_id);

CREATE TABLE usuario_perfis (
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  usuario_id     uuid NOT NULL,
  perfil_id      uuid NOT NULL,
  PRIMARY KEY (organizacao_id, usuario_id, perfil_id),
  FOREIGN KEY (organizacao_id, usuario_id) REFERENCES contas (organizacao_id, id),
  FOREIGN KEY (organizacao_id, perfil_id) REFERENCES perfis (organizacao_id, id) ON DELETE RESTRICT
);

CREATE INDEX usuario_perfis_por_perfil ON usuario_perfis (organizacao_id, perfil_id);

CREATE TABLE usuario_regras (
  id                   uuid PRIMARY KEY,
  organizacao_id       uuid NOT NULL REFERENCES organizacoes (id),
  usuario_id           uuid NOT NULL,
  permissao            text NOT NULL CHECK (length(permissao) BETWEEN 3 AND 120),
  efeito               text NOT NULL CHECK (efeito IN ('allow', 'deny')),
  unidade_id           uuid,
  max_percentual       numeric(5, 2) CHECK (max_percentual BETWEEN 0 AND 100),
  max_valor_centavos   bigint CHECK (max_valor_centavos >= 0),
  teto_diario_centavos bigint CHECK (teto_diario_centavos >= 0),
  CHECK (efeito = 'allow' OR (max_percentual IS NULL AND max_valor_centavos IS NULL AND teto_diario_centavos IS NULL)),
  FOREIGN KEY (organizacao_id, usuario_id) REFERENCES contas (organizacao_id, id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id)
);

CREATE INDEX usuario_regras_por_usuario ON usuario_regras (organizacao_id, usuario_id);

ALTER TABLE perfis         ENABLE ROW LEVEL SECURITY;
ALTER TABLE perfis         FORCE  ROW LEVEL SECURITY;
ALTER TABLE perfil_regras  ENABLE ROW LEVEL SECURITY;
ALTER TABLE perfil_regras  FORCE  ROW LEVEL SECURITY;
ALTER TABLE usuario_perfis ENABLE ROW LEVEL SECURITY;
ALTER TABLE usuario_perfis FORCE  ROW LEVEL SECURITY;
ALTER TABLE usuario_regras ENABLE ROW LEVEL SECURITY;
ALTER TABLE usuario_regras FORCE  ROW LEVEL SECURITY;

CREATE POLICY isolamento ON perfis
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
CREATE POLICY isolamento ON perfil_regras
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
CREATE POLICY isolamento ON usuario_perfis
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
CREATE POLICY isolamento ON usuario_regras
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

-- ---- Session invalidation (spec 5.4.9) ----
-- Bumps the credential version of one account. Invoker rights: RLS keeps it inside the organization of the transaction.
CREATE FUNCTION permissoes_invalidar_conta(p_organizacao uuid, p_conta uuid) RETURNS void
LANGUAGE sql
SET search_path = pg_catalog, pg_temp
AS $$
  UPDATE public.contas SET versao_credencial = versao_credencial + 1 WHERE organizacao_id = p_organizacao AND id = p_conta;
$$;

-- Bumps every account that holds the profile or holds a profile that FOLLOWS it as its template.
CREATE FUNCTION permissoes_invalidar_perfil(p_organizacao uuid, p_perfil uuid) RETURNS void
LANGUAGE sql
SET search_path = pg_catalog, pg_temp
AS $$
  UPDATE public.contas c SET versao_credencial = c.versao_credencial + 1
   WHERE c.organizacao_id = p_organizacao
     AND c.id IN (
       SELECT up.usuario_id
         FROM public.usuario_perfis up
        WHERE up.organizacao_id = p_organizacao
          AND (up.perfil_id = p_perfil
               OR up.perfil_id IN (SELECT p.id FROM public.perfis p
                                    WHERE p.organizacao_id = p_organizacao AND p.acompanha_modelo AND p.modelo = p_perfil::text)));
$$;

CREATE FUNCTION permissoes_gatilho_usuario_perfis() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    PERFORM public.permissoes_invalidar_conta(OLD.organizacao_id, OLD.usuario_id);
    RETURN OLD;
  END IF;
  PERFORM public.permissoes_invalidar_conta(NEW.organizacao_id, NEW.usuario_id);
  RETURN NEW;
END;
$$;

CREATE FUNCTION permissoes_gatilho_usuario_regras() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    PERFORM public.permissoes_invalidar_conta(OLD.organizacao_id, OLD.usuario_id);
    RETURN OLD;
  END IF;
  PERFORM public.permissoes_invalidar_conta(NEW.organizacao_id, NEW.usuario_id);
  RETURN NEW;
END;
$$;

CREATE FUNCTION permissoes_gatilho_perfil_regras() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    PERFORM public.permissoes_invalidar_perfil(OLD.organizacao_id, OLD.perfil_id);
    RETURN OLD;
  END IF;
  PERFORM public.permissoes_invalidar_perfil(NEW.organizacao_id, NEW.perfil_id);
  RETURN NEW;
END;
$$;

-- A change of template (or of the follow flag) changes the rules of every holder; deleting a template changes the profiles that followed it.
CREATE FUNCTION permissoes_gatilho_perfis() RETURNS trigger
LANGUAGE plpgsql
SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
  IF TG_OP = 'DELETE' THEN
    PERFORM public.permissoes_invalidar_perfil(OLD.organizacao_id, OLD.id);
    RETURN OLD;
  END IF;
  IF NEW.modelo IS DISTINCT FROM OLD.modelo OR NEW.acompanha_modelo IS DISTINCT FROM OLD.acompanha_modelo THEN
    PERFORM public.permissoes_invalidar_perfil(NEW.organizacao_id, NEW.id);
  END IF;
  RETURN NEW;
END;
$$;

CREATE TRIGGER usuario_perfis_invalida_sessoes
  AFTER INSERT OR DELETE ON usuario_perfis
  FOR EACH ROW EXECUTE FUNCTION permissoes_gatilho_usuario_perfis();
CREATE TRIGGER usuario_regras_invalida_sessoes
  AFTER INSERT OR UPDATE OR DELETE ON usuario_regras
  FOR EACH ROW EXECUTE FUNCTION permissoes_gatilho_usuario_regras();
CREATE TRIGGER perfil_regras_invalida_sessoes
  AFTER INSERT OR UPDATE OR DELETE ON perfil_regras
  FOR EACH ROW EXECUTE FUNCTION permissoes_gatilho_perfil_regras();
CREATE TRIGGER perfis_invalida_sessoes
  AFTER UPDATE OR DELETE ON perfis
  FOR EACH ROW EXECUTE FUNCTION permissoes_gatilho_perfis();
