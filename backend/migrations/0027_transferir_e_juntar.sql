-- Comandas completas III (F4-0060): merging a comanda into another.
--   comandas.estado                  gains 'juntada': a comanda closed by a merge. Its launched items moved to the other comanda, so it keeps no balance; its cancelled items, rounds and history stay on it.
--   comandas.juntada_na_comanda_id   the comanda that received it (composite FK, so the database refuses a comanda of another organization).
--   comandas.juntada_por             who merged it (composite FK to contas).
-- Both columns are present exactly when estado = 'juntada'. fechada_em keeps meaning "the instant it left the open state", so the existing CHECK (estado = 'aberta') = (fechada_em IS NULL) holds.
-- Transferring and separating items need no schema change: an item moves by changing comanda_id (and rodada_id, to a copy of its round on the destination), both already written by the application.
ALTER TABLE comandas DROP CONSTRAINT comandas_estado_check;
ALTER TABLE comandas ADD CONSTRAINT comandas_estado_check CHECK (estado IN ('aberta', 'fechada', 'cancelada', 'juntada'));
ALTER TABLE comandas ADD COLUMN juntada_na_comanda_id uuid;
ALTER TABLE comandas ADD COLUMN juntada_por uuid;
ALTER TABLE comandas ADD FOREIGN KEY (organizacao_id, juntada_na_comanda_id) REFERENCES comandas (organizacao_id, id);
ALTER TABLE comandas ADD FOREIGN KEY (organizacao_id, juntada_por) REFERENCES contas (organizacao_id, id);
ALTER TABLE comandas ADD CONSTRAINT comandas_juntada_completa
  CHECK ((estado = 'juntada') = (juntada_na_comanda_id IS NOT NULL AND juntada_por IS NOT NULL));
ALTER TABLE comandas ADD CONSTRAINT comandas_juntada_em_outra CHECK (juntada_na_comanda_id IS DISTINCT FROM id);
