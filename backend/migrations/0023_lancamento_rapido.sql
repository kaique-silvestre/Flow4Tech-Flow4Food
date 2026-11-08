-- Lancamento rapido (F4-0052): search by name without accents or case, and favorites per waiter.
--   normaliza_busca(text)         upper case without the diacritics of Latin-1 and Latin Extended-A, whatever the database collation (upper() alone is ASCII-only under the C collation).
--                                 The API normalizes the typed text the same way (RegrasDeBusca.Normalizar), so both sides compare the same form.
--   produtos.nome_busca           generated from the name: no write path changes, never stale.
--   produtos_frequencia_garcom    how many times a waiter launched a product in a unit (fed on launch next to produtos_frequencia); feeds the waiter's favorites.
--                                 History before this migration is not backfilled (RLS hides the rounds from the migration credential); favorites start empty and fill as they launch.
CREATE FUNCTION normaliza_busca(texto text) RETURNS text
  LANGUAGE sql IMMUTABLE PARALLEL SAFE STRICT
  AS $$ SELECT upper(translate(texto, 'ÀÁÂÃÄÅÇÈÉÊËÌÍÎÏÑÒÓÔÕÖÙÚÛÜÝàáâãäåçèéêëìíîïñòóôõöùúûüýÿĀāĂăĄąĆćĈĉĊċČčĎďĒēĔĕĖėĘęĚěĜĝĞğĠġĢģĤĥĨĩĪīĬĭĮįİĴĵĶķĹĺĻļĽľŃńŅņŇňŌōŎŏŐőŔŕŖŗŘřŚśŜŝŞşŠšŢţŤťŨũŪūŬŭŮůŰűŲųŴŵŶŷŸŹźŻżŽž', 'AAAAAACEEEEIIIINOOOOOUUUUYAAAAAACEEEEIIIINOOOOOUUUUYYAAAAAACCCCCCCCDDEEEEEEEEEEGGGGGGGGHHIIIIIIIIIJJKKLLLLLLNNNNNNOOOOOORRRRRRSSSSSSSSTTTTUUUUUUUUUUUUWWYYYZZZZZZ')) $$;

ALTER TABLE produtos ADD COLUMN nome_busca text GENERATED ALWAYS AS (normaliza_busca(nome)) STORED NOT NULL;

CREATE TABLE produtos_frequencia_garcom (
  organizacao_id uuid   NOT NULL REFERENCES organizacoes (id),
  unidade_id     uuid   NOT NULL,
  garcom_id      uuid   NOT NULL,
  produto_id     uuid   NOT NULL,
  lancamentos    bigint NOT NULL CHECK (lancamentos >= 1),
  PRIMARY KEY (organizacao_id, unidade_id, garcom_id, produto_id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, garcom_id) REFERENCES contas (organizacao_id, id),
  FOREIGN KEY (organizacao_id, produto_id) REFERENCES produtos (organizacao_id, id)
);

ALTER TABLE produtos_frequencia_garcom ENABLE ROW LEVEL SECURITY;
ALTER TABLE produtos_frequencia_garcom FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON produtos_frequencia_garcom
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

-- "Last time at this table": the closed comandas of a table in a unit, newest first.
CREATE INDEX comandas_fechadas_por_mesa ON comandas (organizacao_id, unidade_id, mesa_id, fechada_em DESC) WHERE estado = 'fechada' AND mesa_id IS NOT NULL;
