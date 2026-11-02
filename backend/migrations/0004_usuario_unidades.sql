-- Ticket 01 (plataforma base, spec 5.1): link between a user and the units they may work in. Tenant table, same RLS shape as amostra_itens.
-- usuario_id has no foreign key yet: the identity tables arrive with ticket 03, which adds it in its own migration.
-- The composite FK (organizacao_id, unidade_id) makes it impossible to link a user to a unit of ANOTHER organization (FK checks bypass RLS).
CREATE TABLE usuario_unidades (
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  usuario_id     uuid NOT NULL,
  unidade_id     uuid NOT NULL,
  PRIMARY KEY (organizacao_id, usuario_id, unidade_id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id)
);

ALTER TABLE usuario_unidades ENABLE ROW LEVEL SECURITY;
ALTER TABLE usuario_unidades FORCE  ROW LEVEL SECURITY;

CREATE POLICY isolamento ON usuario_unidades
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
