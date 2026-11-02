-- Cumulative authorization limits (ticket 09, ADR-006). One counter row per (organization, user, permission, day): the amount already
-- consumed, e.g. cents of courtesies given today. Tenant table: same RLS shape as amostra_itens.
CREATE TABLE limites_consumo (
  organizacao_id     uuid   NOT NULL REFERENCES organizacoes (id),
  usuario_id         uuid   NOT NULL,
  permissao          text   NOT NULL,
  dia                date   NOT NULL,
  consumido_centavos bigint NOT NULL CHECK (consumido_centavos >= 0),
  -- The key IS the lock target: the atomic upsert in the store serializes concurrent consumers of the same counter on this row.
  PRIMARY KEY (organizacao_id, usuario_id, permissao, dia)
);

ALTER TABLE limites_consumo ENABLE ROW LEVEL SECURITY;
ALTER TABLE limites_consumo FORCE  ROW LEVEL SECURITY;

CREATE POLICY isolamento ON limites_consumo
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
