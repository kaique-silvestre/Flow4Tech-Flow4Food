-- Dominio do salao, ticket 05: comandas (open bills) and the per-day display-number counter (tenant tables, exact RLS, composite FKs).
--   comanda_numeracao  one row per unit and OPERATING day holding the last display number handed out. Allocating is one upsert (`ultimo = ultimo + 1`) inside the opening
--                      transaction: the row lock serializes concurrent opens and a rollback gives the number back, so numbers are unique and gap-free per unit and day.
--   comandas           numero is unique per unit and operating day (restarts each day). identificacao is the table's name or a free name ("Comanda 7"). mesa_id is optional; several
--                      comandas may hold one table. canal is reserved (default 'salao'). taxa_servico_percentual is FROZEN when the comanda opens. versao is the optimistic-concurrency
--                      counter (every write bumps it). A comanda is 'aberta' exactly while fechada_em is null.
CREATE TABLE comanda_numeracao (
  organizacao_id  uuid    NOT NULL REFERENCES organizacoes (id),
  unidade_id      uuid    NOT NULL,
  dia_operacional date    NOT NULL,
  ultimo          integer NOT NULL CHECK (ultimo >= 1),
  PRIMARY KEY (organizacao_id, unidade_id, dia_operacional),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id)
);

ALTER TABLE comanda_numeracao ENABLE ROW LEVEL SECURITY;
ALTER TABLE comanda_numeracao FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON comanda_numeracao
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

-- A comanda may only hold a table of ITS OWN unit: the composite key below lets comandas reference (organizacao_id, unidade_id, id), so the database refuses a table of a sibling unit.
ALTER TABLE mesas ADD CONSTRAINT mesas_organizacao_unidade_id_key UNIQUE (organizacao_id, unidade_id, id);

CREATE TABLE comandas (
  id                      uuid PRIMARY KEY,
  organizacao_id          uuid        NOT NULL REFERENCES organizacoes (id),
  unidade_id              uuid        NOT NULL,
  dia_operacional         date        NOT NULL,
  numero                  integer     NOT NULL CHECK (numero >= 1),
  identificacao           text        NOT NULL CHECK (length(identificacao) BETWEEN 1 AND 40),
  mesa_id                 uuid,
  canal                   text        NOT NULL DEFAULT 'salao' CHECK (length(canal) BETWEEN 1 AND 20),
  garcom_id               uuid        NOT NULL,
  pessoas                 integer     CHECK (pessoas BETWEEN 1 AND 999),
  estado                  text        NOT NULL DEFAULT 'aberta' CHECK (estado IN ('aberta', 'fechada', 'cancelada')),
  aberta_em               timestamptz NOT NULL,
  fechada_em              timestamptz,
  taxa_servico_percentual numeric(5, 2) NOT NULL CHECK (taxa_servico_percentual BETWEEN 0 AND 20),
  taxa_servico_removida   boolean     NOT NULL DEFAULT false,
  versao                  integer     NOT NULL DEFAULT 1 CHECK (versao >= 1),
  UNIQUE (organizacao_id, id),
  UNIQUE (organizacao_id, unidade_id, dia_operacional, numero),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, unidade_id, mesa_id) REFERENCES mesas (organizacao_id, unidade_id, id),
  FOREIGN KEY (organizacao_id, garcom_id) REFERENCES contas (organizacao_id, id),
  CHECK ((estado = 'aberta') = (fechada_em IS NULL))
);

CREATE INDEX comandas_abertas_por_unidade ON comandas (organizacao_id, unidade_id, aberta_em DESC) WHERE estado = 'aberta';
CREATE INDEX comandas_abertas_por_mesa ON comandas (organizacao_id, mesa_id) WHERE estado = 'aberta' AND mesa_id IS NOT NULL;

ALTER TABLE comandas ENABLE ROW LEVEL SECURITY;
ALTER TABLE comandas FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON comandas
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
