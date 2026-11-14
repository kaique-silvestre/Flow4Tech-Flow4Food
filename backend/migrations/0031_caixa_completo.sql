-- Caixa completo (F4-0065): suprimento, sangria, contagem por metodo, tolerancia e aprovacao da diferenca.
--   movimentos_caixa      money in (suprimento) and out (sangria) of the drawer during a shift, with reason and destination. Append-only (the app credential has only SELECT and INSERT). The expected
--                         cash at closing is fundo + cash received + suprimentos - sangrias, summed by the database in the closing statement. destino is required for a sangria.
--   contagens_turno       the count of each NON-cash method at closing (the cash count stays in turnos_caixa). sequencia 1 is the closing, 2 and up are recounts; the current count of a method is
--                         its highest sequencia. Append-only. esperado is what Pagamentos recorded for the shift and method (applied + tip, confirmed only).
--   aprovacoes_diferenca  what an approver did about a difference above the tolerance: accepted it ('aprovacao') or counted again ('recontagem'). Append-only.
--   turnos_caixa          suprimentos/sangrias snapshot at closing (null while open) and situacao_conferencia: 'concluida' (inside the tolerance), 'pendente' (waiting for an approver), 'aprovada'.
--                         null while open and on shifts closed before this migration (read as concluida).
CREATE TABLE movimentos_caixa (
  id              uuid PRIMARY KEY,
  organizacao_id  uuid        NOT NULL REFERENCES organizacoes (id),
  unidade_id      uuid        NOT NULL,
  turno_id        uuid        NOT NULL,
  tipo            text        NOT NULL CHECK (tipo IN ('suprimento', 'sangria')),
  valor_centavos  bigint      NOT NULL CHECK (valor_centavos >= 1),
  motivo          text        NOT NULL CHECK (length(motivo) BETWEEN 1 AND 200 AND motivo = btrim(motivo)),
  destino         text        CHECK (destino IS NULL OR (length(destino) BETWEEN 1 AND 80 AND destino = btrim(destino))),
  operador_id     uuid        NOT NULL,
  criado_em       timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  CHECK (tipo = 'suprimento' OR destino IS NOT NULL),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, turno_id) REFERENCES turnos_caixa (organizacao_id, id),
  FOREIGN KEY (organizacao_id, operador_id) REFERENCES contas (organizacao_id, id)
);

CREATE INDEX movimentos_caixa_por_turno ON movimentos_caixa (organizacao_id, turno_id, criado_em, id);

CREATE TABLE contagens_turno (
  organizacao_id    uuid        NOT NULL REFERENCES organizacoes (id),
  turno_id          uuid        NOT NULL,
  metodo_id         uuid        NOT NULL,
  sequencia         integer     NOT NULL CHECK (sequencia >= 1),
  metodo_nome       text        NOT NULL CHECK (length(metodo_nome) BETWEEN 1 AND 40),
  contado_centavos  bigint      NOT NULL CHECK (contado_centavos >= 0),
  esperado_centavos bigint      NOT NULL CHECK (esperado_centavos >= 0),
  criado_em         timestamptz NOT NULL,
  PRIMARY KEY (organizacao_id, turno_id, metodo_id, sequencia),
  FOREIGN KEY (organizacao_id, turno_id) REFERENCES turnos_caixa (organizacao_id, id),
  FOREIGN KEY (organizacao_id, metodo_id) REFERENCES metodos_pagamento (organizacao_id, id)
);

CREATE TABLE aprovacoes_diferenca (
  id                         uuid PRIMARY KEY,
  organizacao_id             uuid        NOT NULL REFERENCES organizacoes (id),
  unidade_id                 uuid        NOT NULL,
  turno_id                   uuid        NOT NULL,
  tipo                       text        NOT NULL CHECK (tipo IN ('aprovacao', 'recontagem')),
  aprovador_id               uuid        NOT NULL,
  motivo                     text        NOT NULL CHECK (length(motivo) BETWEEN 1 AND 200 AND motivo = btrim(motivo)),
  diferenca_antes_centavos   bigint      NOT NULL CHECK (diferenca_antes_centavos >= 0),
  diferenca_depois_centavos  bigint      NOT NULL CHECK (diferenca_depois_centavos >= 0),
  criado_em                  timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, turno_id) REFERENCES turnos_caixa (organizacao_id, id),
  FOREIGN KEY (organizacao_id, aprovador_id) REFERENCES contas (organizacao_id, id)
);

CREATE INDEX aprovacoes_diferenca_por_turno ON aprovacoes_diferenca (organizacao_id, turno_id, criado_em, id);

ALTER TABLE turnos_caixa
  ADD COLUMN suprimentos_centavos   bigint CHECK (suprimentos_centavos >= 0),
  ADD COLUMN sangrias_centavos      bigint CHECK (sangrias_centavos >= 0),
  ADD COLUMN situacao_conferencia   text   CHECK (situacao_conferencia IN ('concluida', 'pendente', 'aprovada')),
  ADD CONSTRAINT turnos_caixa_conferencia_so_fechado CHECK (fechado_em IS NOT NULL OR (situacao_conferencia IS NULL AND suprimentos_centavos IS NULL AND sangrias_centavos IS NULL));

CREATE INDEX turnos_caixa_pendentes ON turnos_caixa (organizacao_id, unidade_id, fechado_em DESC) WHERE situacao_conferencia = 'pendente';

ALTER TABLE movimentos_caixa ENABLE ROW LEVEL SECURITY;
ALTER TABLE movimentos_caixa FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON movimentos_caixa
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

ALTER TABLE contagens_turno ENABLE ROW LEVEL SECURITY;
ALTER TABLE contagens_turno FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON contagens_turno
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

ALTER TABLE aprovacoes_diferenca ENABLE ROW LEVEL SECURITY;
ALTER TABLE aprovacoes_diferenca FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON aprovacoes_diferenca
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON movimentos_caixa FROM flow4tech_app;
REVOKE ALL ON movimentos_caixa FROM PUBLIC;
REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON contagens_turno FROM flow4tech_app;
REVOKE ALL ON contagens_turno FROM PUBLIC;
REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON aprovacoes_diferenca FROM flow4tech_app;
REVOKE ALL ON aprovacoes_diferenca FROM PUBLIC;
