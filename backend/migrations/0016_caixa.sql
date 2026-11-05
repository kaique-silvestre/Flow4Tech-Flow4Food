-- Dominio do salao, ticket 03: cash shift. One shift = one operator in one unit, opened with a float and closed with a count (tenant table, exact RLS, composite FKs).
--   fundo_centavos      opening float (change fund): NOT a sale, it only enters the expected cash at closing.
--   contado/esperado/diferenca_centavos  set together at closing: difference = counted - expected (negative = shortage). All integer cents, BRL.
--   turnos_caixa_um_aberto  at most one OPEN shift per operator and unit (the guard against two simultaneous opens).
CREATE TABLE turnos_caixa (
  id                 uuid PRIMARY KEY,
  organizacao_id     uuid NOT NULL REFERENCES organizacoes (id),
  unidade_id         uuid NOT NULL,
  operador_id        uuid NOT NULL,
  aberto_em          timestamptz NOT NULL,
  fundo_centavos     bigint NOT NULL CHECK (fundo_centavos >= 0),
  fechado_em         timestamptz,
  contado_centavos   bigint CHECK (contado_centavos >= 0),
  esperado_centavos  bigint CHECK (esperado_centavos >= 0),
  diferenca_centavos bigint,
  UNIQUE (organizacao_id, id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, operador_id) REFERENCES contas (organizacao_id, id),
  CHECK ((fechado_em IS NULL) = (contado_centavos IS NULL)
     AND (fechado_em IS NULL) = (esperado_centavos IS NULL)
     AND (fechado_em IS NULL) = (diferenca_centavos IS NULL)),
  CHECK (fechado_em IS NULL OR (fechado_em >= aberto_em AND diferenca_centavos = contado_centavos - esperado_centavos))
);

CREATE UNIQUE INDEX turnos_caixa_um_aberto ON turnos_caixa (organizacao_id, unidade_id, operador_id) WHERE fechado_em IS NULL;
CREATE INDEX turnos_caixa_por_unidade ON turnos_caixa (organizacao_id, unidade_id, aberto_em DESC);

ALTER TABLE turnos_caixa ENABLE ROW LEVEL SECURITY;
ALTER TABLE turnos_caixa FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON turnos_caixa
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
