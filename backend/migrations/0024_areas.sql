-- Salao rapido, F4-0054: areas of a unit and the link from a table to its area (tenant table, exact RLS, composite FKs).
--   areas        a named zone of the house ("Varanda", "Salao"); name 1 to 40 characters, unique per unit ignoring case. A retired area (ativa = false) is kept for history; its tables
--                are then shown without an area.
--   mesas.area_id  optional; the composite key below lets a table reference (organizacao_id, unidade_id, id), so the database refuses an area of a sibling unit.
-- The table status stays COMPUTED from the open comandas (free, occupied, waiting for the bill): there is no status column anywhere.
CREATE TABLE areas (
  id             uuid PRIMARY KEY,
  organizacao_id uuid        NOT NULL REFERENCES organizacoes (id),
  unidade_id     uuid        NOT NULL,
  nome           text        NOT NULL CHECK (length(nome) BETWEEN 1 AND 40),
  ativa          boolean     NOT NULL DEFAULT true,
  criada_em      timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  UNIQUE (organizacao_id, unidade_id, id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id)
);

CREATE UNIQUE INDEX areas_nome_por_unidade ON areas (organizacao_id, unidade_id, lower(nome));

ALTER TABLE areas ENABLE ROW LEVEL SECURITY;
ALTER TABLE areas FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON areas
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

ALTER TABLE mesas ADD COLUMN area_id uuid;
ALTER TABLE mesas ADD CONSTRAINT mesas_area_fkey
  FOREIGN KEY (organizacao_id, unidade_id, area_id) REFERENCES areas (organizacao_id, unidade_id, id);

CREATE INDEX mesas_por_area ON mesas (organizacao_id, area_id) WHERE area_id IS NOT NULL;
