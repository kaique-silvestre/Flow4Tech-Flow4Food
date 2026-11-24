-- Reservas (F4-0153): bookings of a unit (tenant table, exact RLS, composite FKs).
--   nome, telefone   PERSONAL DATA of the guest. They live only here: never in the audit trail, the outbox or the technical log.
--   pessoas          head count, 1 to 999 (becomes the comanda's head count on arrival).
--   horario          the booked instant (UTC); dia_operacional is the unit's operating day of that instant, fixed when booked.
--   mesa_id          optional; the composite key lets the database refuse a table of a sibling unit. Only a 'reservada' (waiting) booking holds its table: the overlap check is made by the
--                    use case under an advisory lock on the table (no exclusion constraint: it would need btree_gist).
--   estado           reservada -> chegou | cancelada | nao-compareceu (final). comanda_id is set exactly when estado = 'chegou'.
CREATE TABLE reservas (
  id              uuid PRIMARY KEY,
  organizacao_id  uuid        NOT NULL REFERENCES organizacoes (id),
  unidade_id      uuid        NOT NULL,
  mesa_id         uuid,
  nome            text        NOT NULL CHECK (length(nome) BETWEEN 1 AND 80),
  telefone        text        CHECK (length(telefone) BETWEEN 1 AND 30),
  pessoas         integer     NOT NULL CHECK (pessoas BETWEEN 1 AND 999),
  horario         timestamptz NOT NULL,
  dia_operacional date        NOT NULL,
  observacao      text        CHECK (length(observacao) BETWEEN 1 AND 200),
  estado          text        NOT NULL DEFAULT 'reservada' CHECK (estado IN ('reservada', 'chegou', 'cancelada', 'nao-compareceu')),
  comanda_id      uuid,
  criada_em       timestamptz NOT NULL,
  atualizada_em   timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, unidade_id, mesa_id) REFERENCES mesas (organizacao_id, unidade_id, id),
  FOREIGN KEY (organizacao_id, comanda_id) REFERENCES comandas (organizacao_id, id),
  CHECK ((estado = 'chegou') = (comanda_id IS NOT NULL))
);

CREATE INDEX reservas_do_dia ON reservas (organizacao_id, unidade_id, dia_operacional, horario);
CREATE INDEX reservas_em_espera_por_mesa ON reservas (organizacao_id, mesa_id, horario) WHERE estado = 'reservada' AND mesa_id IS NOT NULL;

ALTER TABLE reservas ENABLE ROW LEVEL SECURITY;
ALTER TABLE reservas FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON reservas
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
