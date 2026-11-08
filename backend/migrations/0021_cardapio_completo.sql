-- Cardapio completo (F4-0048): subcategories (two levels), promotional price with a validity window, per-unit price override and the manual "acabou" switch.
--   categorias.categoria_pai_id  null = top level; else the parent, which must itself be top level (a trigger keeps the tree at two levels, whatever the writer).
--   produtos.preco_promocional_* promotional price in cents, valid in [promocao_inicio, promocao_fim) (UTC); all three set or all three null.
--   produtos.indisponivel        manual "acabou": the product stays on the menu but cannot be launched.
--   produtos_preco_unidade       (organizacao, unidade, produto) -> price in cents, overrides the base price (and any promotion) in that unit only.
--   The price frozen in comanda items stays the source of what was charged; none of this reaches an item already launched.
ALTER TABLE categorias
  ADD COLUMN categoria_pai_id uuid NULL,
  ADD CONSTRAINT categorias_pai_fk FOREIGN KEY (organizacao_id, categoria_pai_id) REFERENCES categorias (organizacao_id, id),
  ADD CONSTRAINT categorias_pai_diferente CHECK (categoria_pai_id IS NULL OR categoria_pai_id <> id);

CREATE FUNCTION categorias_dois_niveis() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.categoria_pai_id IS NOT NULL THEN
    -- Lock the parent row and this row, so two concurrent writes (A under B, B under A) serialize and the checks below see each other's result.
    PERFORM 1 FROM categorias WHERE organizacao_id = NEW.organizacao_id AND id IN (NEW.categoria_pai_id, NEW.id) ORDER BY id FOR UPDATE;
    -- The parent must be top level, and a category that already has children cannot become a child.
    IF EXISTS (SELECT 1 FROM categorias p WHERE p.organizacao_id = NEW.organizacao_id AND p.id = NEW.categoria_pai_id AND p.categoria_pai_id IS NOT NULL) THEN
      RAISE EXCEPTION 'categoria com mais de dois niveis' USING ERRCODE = '23514';
    END IF;
    IF EXISTS (SELECT 1 FROM categorias f WHERE f.organizacao_id = NEW.organizacao_id AND f.categoria_pai_id = NEW.id) THEN
      RAISE EXCEPTION 'categoria com subcategorias nao pode ter pai' USING ERRCODE = '23514';
    END IF;
  END IF;
  RETURN NEW;
END $$;

CREATE TRIGGER categorias_dois_niveis BEFORE INSERT OR UPDATE OF categoria_pai_id ON categorias
  FOR EACH ROW EXECUTE FUNCTION categorias_dois_niveis();

ALTER TABLE produtos
  ADD COLUMN preco_promocional_centavos bigint NULL CHECK (preco_promocional_centavos >= 0),
  ADD COLUMN promocao_inicio timestamptz NULL,
  ADD COLUMN promocao_fim timestamptz NULL,
  ADD COLUMN indisponivel boolean NOT NULL DEFAULT false,
  ADD CONSTRAINT produtos_promocao_completa CHECK (
    (preco_promocional_centavos IS NULL AND promocao_inicio IS NULL AND promocao_fim IS NULL)
    OR (preco_promocional_centavos IS NOT NULL AND promocao_inicio IS NOT NULL AND promocao_fim IS NOT NULL AND promocao_fim > promocao_inicio));

CREATE TABLE produtos_preco_unidade (
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  unidade_id     uuid NOT NULL,
  produto_id     uuid NOT NULL,
  preco_centavos bigint NOT NULL CHECK (preco_centavos >= 0),
  atualizado_em  timestamptz NOT NULL,
  PRIMARY KEY (organizacao_id, unidade_id, produto_id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, produto_id) REFERENCES produtos (organizacao_id, id)
);

ALTER TABLE produtos_preco_unidade ENABLE ROW LEVEL SECURITY;
ALTER TABLE produtos_preco_unidade FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON produtos_preco_unidade
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
