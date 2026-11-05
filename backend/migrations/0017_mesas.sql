-- Dominio do salao, ticket 04: tables (mesas) of a unit. Optional register; there is NO status column: free/occupied is computed from open comandas (a later ticket).
--   identificacao  number or name as the house calls it ("12", "Varanda 3"), 1 to 40 characters, unique per unit ignoring case.
--   capacidade     optional seats, 1 to 999.
--   ativa          a retired table stays registered (history) but is not listed to the waiter.
CREATE TABLE mesas (
  id             uuid PRIMARY KEY,
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  unidade_id     uuid NOT NULL,
  identificacao  text NOT NULL CHECK (length(identificacao) BETWEEN 1 AND 40),
  capacidade     integer CHECK (capacidade BETWEEN 1 AND 999),
  ativa          boolean NOT NULL DEFAULT true,
  criada_em      timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id)
);

CREATE UNIQUE INDEX mesas_identificacao_por_unidade ON mesas (organizacao_id, unidade_id, lower(identificacao));

ALTER TABLE mesas ENABLE ROW LEVEL SECURITY;
ALTER TABLE mesas FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON mesas
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
