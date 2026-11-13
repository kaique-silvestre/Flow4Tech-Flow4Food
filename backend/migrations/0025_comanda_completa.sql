-- Comandas completas I (F4-0056): general note and cancellation of a comanda.
--   comandas.observacao           optional free note of the whole comanda (1 to 200 characters); null when there is none.
--   comandas.motivo_cancelamento  reason of a cancelled comanda (the standard list word, or the free text of 'outro'); present exactly when estado = 'cancelada'.
--   comandas.cancelada_por        who cancelled it (composite FK to contas, so an account of another organization is refused by the database).
-- A cancelled comanda keeps its row, its items and its history; `fechada_em` is the instant it left the open state (closed or cancelled), the existing CHECK keeps
-- `(estado = 'aberta') = (fechada_em IS NULL)`. There is no new RLS or privilege: the table is the one that already has the exact policy.
ALTER TABLE comandas ADD COLUMN observacao text CHECK (length(observacao) BETWEEN 1 AND 200);
ALTER TABLE comandas ADD COLUMN motivo_cancelamento text CHECK (length(motivo_cancelamento) BETWEEN 1 AND 200);
ALTER TABLE comandas ADD COLUMN cancelada_por uuid;
ALTER TABLE comandas ADD FOREIGN KEY (organizacao_id, cancelada_por) REFERENCES contas (organizacao_id, id);
ALTER TABLE comandas ADD CONSTRAINT comandas_cancelamento_completo
  CHECK ((estado = 'cancelada') = (motivo_cancelamento IS NOT NULL AND cancelada_por IS NOT NULL));
