-- Pre-bill and 'bill requested' (F4-0067).
--   comandas.conta_pedida_em   the instant someone asked for the bill of an OPEN comanda (the table is then "waiting for the bill"); null when nobody did.
--   comandas.conta_pedida_por  who asked (composite FK to contas, so an account of another organization is refused by the database).
-- Both are present together, and only while the comanda is open: closing, cancelling or merging clears them in the same UPDATE (the CHECK below makes the database refuse a closed comanda that still carries the mark).
-- No new table, RLS or privilege: the column belongs to the table that already has the exact policy. The mark does not bump `versao` (it changes no bill).
ALTER TABLE comandas ADD COLUMN conta_pedida_em timestamptz;
ALTER TABLE comandas ADD COLUMN conta_pedida_por uuid;
ALTER TABLE comandas ADD FOREIGN KEY (organizacao_id, conta_pedida_por) REFERENCES contas (organizacao_id, id);
ALTER TABLE comandas ADD CONSTRAINT comandas_conta_pedida_completa
  CHECK ((conta_pedida_em IS NULL) = (conta_pedida_por IS NULL));
ALTER TABLE comandas ADD CONSTRAINT comandas_conta_pedida_so_aberta
  CHECK (conta_pedida_em IS NULL OR estado = 'aberta');
