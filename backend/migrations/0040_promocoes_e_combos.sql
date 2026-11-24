-- Promocoes e combos (F4-0151, ADR-020): a persisted rule, evaluated by the SERVER when a round is launched; the item keeps the promotion that was applied.
--   promocoes          one row per rule. tipo is 'percentual' (percentual off each unit of the listed products), 'valor' (valor_centavos off each unit) or 'combo' (the listed products at the fixed price
--                      valor_centavos for one full set). unidade_id null = every unit of the organization. The window is: ativo, an absolute validity (valido_de inclusive, valido_ate exclusive), the days
--                      of the week (bitmask, bit 0 = Sunday .. bit 6 = Saturday) and an optional time window in the unit's local time (hora_fim <= hora_inicio crosses midnight; the days are the START day).
--                      The rule is edited in place (versao guards a concurrent edit; the app credential cannot DELETE: turn it off instead); its products are written once.
--   promocao_produtos  the products of a rule and how many units a set needs (always 1 outside a combo). Written once (SELECT and INSERT only).
--   itens_comanda      promocao_id, promocao_nome and promocao_centavos: the promotion applied to the line when it was launched and what it took off (promocao_centavos is always also a row of
--                      descontos_comanda with promocao_id set, so the bill uses the ordinary discount path of ADR-018, before any manual discount).
--   descontos_comanda  promocao_id: the discount was given by a promotion (item_id is then never null).
-- Everything is tenant data: exact RLS, composite FKs.
CREATE TABLE promocoes (
  id              uuid PRIMARY KEY,
  organizacao_id  uuid        NOT NULL REFERENCES organizacoes (id),
  unidade_id      uuid,
  nome            text        NOT NULL CHECK (length(btrim(nome)) BETWEEN 1 AND 80),
  tipo            text        NOT NULL CHECK (tipo IN ('percentual', 'valor', 'combo')),
  percentual      numeric(5, 2) CHECK (percentual > 0 AND percentual <= 100),
  valor_centavos  bigint      CHECK (valor_centavos >= 1),
  ativo           boolean     NOT NULL,
  valido_de       timestamptz,
  valido_ate      timestamptz,
  dias_semana     smallint    NOT NULL DEFAULT 127 CHECK (dias_semana BETWEEN 1 AND 127),
  hora_inicio     time,
  hora_fim        time,
  versao          integer     NOT NULL DEFAULT 1 CHECK (versao >= 1),
  criado_por      uuid        NOT NULL,
  criado_em       timestamptz NOT NULL,
  atualizado_por  uuid        NOT NULL,
  atualizado_em   timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  CHECK ((tipo = 'percentual') = (percentual IS NOT NULL)),
  CHECK ((tipo = 'percentual') = (valor_centavos IS NULL)),
  CHECK (valido_de IS NULL OR valido_ate IS NULL OR valido_de < valido_ate),
  CHECK ((hora_inicio IS NULL) = (hora_fim IS NULL)),
  CHECK (hora_inicio IS NULL OR hora_inicio <> hora_fim),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, criado_por) REFERENCES contas (organizacao_id, id),
  FOREIGN KEY (organizacao_id, atualizado_por) REFERENCES contas (organizacao_id, id)
);

CREATE INDEX promocoes_por_organizacao ON promocoes (organizacao_id, ativo, criado_em, id);

CREATE TABLE promocao_produtos (
  organizacao_id uuid    NOT NULL REFERENCES organizacoes (id),
  promocao_id    uuid    NOT NULL,
  produto_id     uuid    NOT NULL,
  quantidade     integer NOT NULL CHECK (quantidade BETWEEN 1 AND 99),
  PRIMARY KEY (organizacao_id, promocao_id, produto_id),
  FOREIGN KEY (organizacao_id, promocao_id) REFERENCES promocoes (organizacao_id, id),
  FOREIGN KEY (organizacao_id, produto_id) REFERENCES produtos (organizacao_id, id)
);

ALTER TABLE promocoes ENABLE ROW LEVEL SECURITY;
ALTER TABLE promocoes FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON promocoes
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

ALTER TABLE promocao_produtos ENABLE ROW LEVEL SECURITY;
ALTER TABLE promocao_produtos FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON promocao_produtos
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

REVOKE DELETE, TRUNCATE, REFERENCES, TRIGGER ON promocoes FROM flow4tech_app;
REVOKE ALL ON promocoes FROM PUBLIC;
REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON promocao_produtos FROM flow4tech_app;
REVOKE ALL ON promocao_produtos FROM PUBLIC;

ALTER TABLE itens_comanda ADD COLUMN promocao_id uuid;
ALTER TABLE itens_comanda ADD COLUMN promocao_nome text CHECK (length(promocao_nome) BETWEEN 1 AND 80);
ALTER TABLE itens_comanda ADD COLUMN promocao_centavos bigint NOT NULL DEFAULT 0 CHECK (promocao_centavos >= 0);
ALTER TABLE itens_comanda ADD FOREIGN KEY (organizacao_id, promocao_id) REFERENCES promocoes (organizacao_id, id);
ALTER TABLE itens_comanda ADD CONSTRAINT itens_promocao_completa
  CHECK ((promocao_id IS NULL) = (promocao_nome IS NULL) AND (promocao_id IS NOT NULL OR promocao_centavos = 0));

ALTER TABLE descontos_comanda ADD COLUMN promocao_id uuid;
ALTER TABLE descontos_comanda ADD FOREIGN KEY (organizacao_id, promocao_id) REFERENCES promocoes (organizacao_id, id);
ALTER TABLE descontos_comanda ADD CONSTRAINT descontos_promocao_no_item CHECK (promocao_id IS NULL OR item_id IS NOT NULL);
