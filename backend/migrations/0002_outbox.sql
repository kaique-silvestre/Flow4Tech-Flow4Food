-- Transactional outbox (ticket 08, ADR-003). Both tables are tenant tables: same RLS shape as amostra_itens.

-- Domain events, written in the SAME transaction as the state change they describe.
CREATE TABLE outbox_eventos (
  id             uuid PRIMARY KEY,
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  tipo           text NOT NULL,
  payload        jsonb NOT NULL,
  ocorrido_em    timestamptz NOT NULL,
  processado_em  timestamptz,
  tentativas     integer NOT NULL DEFAULT 0,
  falhou_em      timestamptz,
  ultimo_erro    text,
  -- Redundant with the primary key for uniqueness; kept on purpose as the target of composite tenant FKs
  -- (organizacao_id, evento_id) from future child tables (ADR-002 convention: parents carry UNIQUE (organizacao_id, id)).
  UNIQUE (organizacao_id, id)
);
-- The dispatcher only ever looks at rows still to deliver.
CREATE INDEX outbox_eventos_pendentes ON outbox_eventos (organizacao_id, ocorrido_em, id)
  WHERE processado_em IS NULL AND falhou_em IS NULL;

-- Inbox: which consumer already applied which event (idempotent consumers). No FK to outbox_eventos so the outbox can be pruned
-- earlier than the inbox.
CREATE TABLE outbox_consumos (
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  evento_id      uuid NOT NULL,
  consumidor     text NOT NULL,
  consumido_em   timestamptz NOT NULL,
  PRIMARY KEY (organizacao_id, evento_id, consumidor)
);

ALTER TABLE outbox_eventos  ENABLE ROW LEVEL SECURITY;
ALTER TABLE outbox_eventos  FORCE  ROW LEVEL SECURITY;
ALTER TABLE outbox_consumos ENABLE ROW LEVEL SECURITY;
ALTER TABLE outbox_consumos FORCE  ROW LEVEL SECURITY;

CREATE POLICY isolamento ON outbox_eventos
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
CREATE POLICY isolamento ON outbox_consumos
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
