-- Registry of organizations with pending outbox events (ticket 07, spec 5.8, closes F2 of ADR-003).
-- RLS hides outbox_eventos from any connection without an organization context, so the dispatcher cannot ask "who has pending events?". This table
-- answers only that: organization id and an instant, no event data. It is NOT a tenant table (no RLS: no tenant owns the question), so, like
-- identidade_emails, the application role has NO privilege on it and only the SECURITY DEFINER functions below touch it. No role that bypasses RLS exists.

CREATE TABLE outbox_organizacoes_pendentes (
  organizacao_id uuid PRIMARY KEY REFERENCES organizacoes (id),
  -- When the organization was first registered while it had pending events (kept while registered; oldest first for the dispatcher).
  desde          timestamptz NOT NULL
);
REVOKE ALL ON outbox_organizacoes_pendentes FROM PUBLIC;
REVOKE ALL ON outbox_organizacoes_pendentes FROM flow4tech_app;

-- Maintained by a trigger, so the registry row is written by the SAME statement transaction as the event row: it commits or rolls back with it, and no
-- code path that inserts an event can forget it. The no-op DO UPDATE takes the row lock on purpose: a concurrent outbox_encerrar_pendencia() (which
-- locks the row before checking for pending events) then waits for this transaction and sees its event, instead of deleting the row under it.
-- The trigger function runs with the owner's rights; the organization comes from the row being inserted, which RLS (WITH CHECK) already tied to the context.
CREATE FUNCTION outbox_registrar_pendente()
RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
  IF NEW.organizacao_id IS DISTINCT FROM public.app_organizacao_id() THEN
    RAISE EXCEPTION 'evento fora do contexto da organizacao' USING ERRCODE = '42501';
  END IF;
  INSERT INTO public.outbox_organizacoes_pendentes AS r (organizacao_id, desde)
    VALUES (NEW.organizacao_id, NEW.ocorrido_em)
    ON CONFLICT (organizacao_id) DO UPDATE SET desde = r.desde;
  RETURN NULL;
END
$$;

CREATE TRIGGER outbox_eventos_registrar_pendente
  AFTER INSERT ON outbox_eventos
  FOR EACH ROW EXECUTE FUNCTION outbox_registrar_pendente();

-- The dispatcher's discovery: which organizations to scan, oldest registration first. Returns only ids and instants, never event data; the events
-- themselves are still read under RLS, inside a context for ONE organization. Refuses to run inside an organization context (like the pre-authentication
-- functions): a statement injected into a tenant transaction cannot use it. Page size is clamped to 1..1000.
CREATE FUNCTION outbox_organizacoes_pendentes(p_limite integer)
RETURNS TABLE (o_organizacao_id uuid, o_desde timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $$
BEGIN
  IF public.app_organizacao_id() IS NOT NULL THEN
    RAISE EXCEPTION 'a descoberta de organizacoes pendentes nao roda dentro de um contexto de organizacao' USING ERRCODE = '42501';
  END IF;
  RETURN QUERY
    SELECT r.organizacao_id, r.desde
      FROM public.outbox_organizacoes_pendentes r
     ORDER BY r.desde, r.organizacao_id
     LIMIT greatest(1, least(coalesce(p_limite, 100), 1000));
END
$$;

-- Removes the CALLER'S OWN organization from the registry when it has no pending event left. Needs the organization context. Order matters: lock the
-- registry row first, THEN look for pending events (a new statement, so a writer that committed meanwhile is seen). True when the organization is
-- not registered any more. The check reads outbox_eventos under RLS of the current organization only.
CREATE FUNCTION outbox_encerrar_pendencia()
RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
  v_organizacao uuid;
  v_trancada uuid;
BEGIN
  v_organizacao := public.app_organizacao_id();
  IF v_organizacao IS NULL THEN
    RAISE EXCEPTION 'sem contexto de organizacao' USING ERRCODE = '42501';
  END IF;
  SELECT r.organizacao_id INTO v_trancada FROM public.outbox_organizacoes_pendentes r WHERE r.organizacao_id = v_organizacao FOR UPDATE;
  IF NOT FOUND THEN
    RETURN true;
  END IF;
  IF EXISTS (SELECT 1 FROM public.outbox_eventos e WHERE e.organizacao_id = v_organizacao AND e.processado_em IS NULL AND e.falhou_em IS NULL) THEN
    RETURN false;
  END IF;
  DELETE FROM public.outbox_organizacoes_pendentes r WHERE r.organizacao_id = v_organizacao;
  RETURN true;
END
$$;

REVOKE ALL ON FUNCTION outbox_registrar_pendente() FROM PUBLIC;
REVOKE ALL ON FUNCTION outbox_organizacoes_pendentes(integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION outbox_encerrar_pendencia() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION outbox_organizacoes_pendentes(integer) TO flow4tech_app;
GRANT EXECUTE ON FUNCTION outbox_encerrar_pendencia() TO flow4tech_app;

-- Backfill: events already pending before this migration. FORCE binds even the owner, so the toggle is the owner's own DDL, inside this migration's
-- transaction (no role is created, nothing is left bypassed).
ALTER TABLE outbox_eventos NO FORCE ROW LEVEL SECURITY;
INSERT INTO outbox_organizacoes_pendentes (organizacao_id, desde)
  SELECT organizacao_id, min(ocorrido_em) FROM outbox_eventos WHERE processado_em IS NULL AND falhou_em IS NULL GROUP BY organizacao_id;
ALTER TABLE outbox_eventos FORCE ROW LEVEL SECURITY;
