-- Ticket 02 (review fixes): state of the audit chain verifier, one row per organization. Tenant table, exact RLS shape of amostra_itens.
--
-- ancora_*: head (sequence + hash) of the last VALID verification. The next verification uses it to see a removed tail, which the chain alone cannot prove.
--           LIMIT: it lives in the same database, so it only protects against someone who cannot also rewrite this row (the OWNER credential can);
--           the copy outside the database stays the spec's debt (5.6.5, open question 3).
-- violacao_*: the violation already alerted (first broken sequence, its kind, and a fingerprint of the whole reported set), so a distinct violation
--           raises one outbox event instead of one per run. Cleared when the chain verifies valid again.
-- The application must INSERT and UPDATE this row, so unlike auditoria_registros it is not append-only; it can still never DELETE or TRUNCATE it.
-- Rewriting it does not forge anything in the trail itself: the chain is verified from the records, this row only adds the tail check and the alert dedupe.
CREATE TABLE auditoria_verificacao (
  organizacao_id        uuid PRIMARY KEY REFERENCES organizacoes (id),
  ancora_sequencia      bigint CHECK (ancora_sequencia >= 1),
  ancora_hash           bytea  CHECK (octet_length(ancora_hash) = 32),
  violacao_sequencia    bigint,
  violacao_tipo         text,
  violacao_assinatura   text,
  verificado_em         timestamptz NOT NULL,
  CHECK ((ancora_sequencia IS NULL) = (ancora_hash IS NULL)),
  CHECK ((violacao_assinatura IS NULL) = (violacao_sequencia IS NULL))
);

ALTER TABLE auditoria_verificacao ENABLE ROW LEVEL SECURITY;
ALTER TABLE auditoria_verificacao FORCE  ROW LEVEL SECURITY;

CREATE POLICY isolamento ON auditoria_verificacao
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

REVOKE DELETE, TRUNCATE, REFERENCES, TRIGGER ON auditoria_verificacao FROM flow4tech_app;
REVOKE ALL ON auditoria_verificacao FROM PUBLIC;
