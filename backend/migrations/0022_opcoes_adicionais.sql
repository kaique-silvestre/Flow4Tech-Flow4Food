-- Opcoes e adicionais (F4-0050): option groups with min/max, their options (add-on with a price, removal at price zero), the links to products,
-- the options frozen into a launched item, and the usage count that feeds "most used add-ons". Tenant tables, exact RLS, composite FKs.
--   grupos_opcao           minimo/maximo of choices (minimo >= 1 means required); ativo turns the whole group off.
--   opcoes                 never deleted (launched items point at them): ativa = false retires one.
--   produto_grupos_opcao   which groups a product offers, in display order.
--   item_opcoes            the option chosen for an item: name and price are COPIED at launch (a later change never reaches the bill). One row per option per item.
--   opcoes_frequencia      launches counted per (unit, product, option); written when a round is launched.
CREATE TABLE grupos_opcao (
  id             uuid PRIMARY KEY,
  organizacao_id uuid        NOT NULL REFERENCES organizacoes (id),
  nome           text        NOT NULL CHECK (length(nome) BETWEEN 1 AND 80),
  minimo         integer     NOT NULL CHECK (minimo >= 0),
  maximo         integer     NOT NULL CHECK (maximo BETWEEN 1 AND 50),
  ativo          boolean     NOT NULL DEFAULT true,
  versao         integer     NOT NULL DEFAULT 1 CHECK (versao >= 1),
  criado_em      timestamptz NOT NULL,
  atualizado_em  timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  CHECK (minimo <= maximo)
);

CREATE TABLE opcoes (
  id             uuid PRIMARY KEY,
  organizacao_id uuid        NOT NULL REFERENCES organizacoes (id),
  grupo_id       uuid        NOT NULL,
  nome           text        NOT NULL CHECK (length(nome) BETWEEN 1 AND 80),
  preco_centavos bigint      NOT NULL CHECK (preco_centavos >= 0),
  ativa          boolean     NOT NULL DEFAULT true,
  posicao        integer     NOT NULL CHECK (posicao >= 1),
  UNIQUE (organizacao_id, id),
  FOREIGN KEY (organizacao_id, grupo_id) REFERENCES grupos_opcao (organizacao_id, id)
);

CREATE INDEX opcoes_por_grupo ON opcoes (organizacao_id, grupo_id, posicao);

CREATE TABLE produto_grupos_opcao (
  organizacao_id uuid    NOT NULL REFERENCES organizacoes (id),
  produto_id     uuid    NOT NULL,
  grupo_id       uuid    NOT NULL,
  posicao        integer NOT NULL CHECK (posicao >= 1),
  PRIMARY KEY (organizacao_id, produto_id, grupo_id),
  FOREIGN KEY (organizacao_id, produto_id) REFERENCES produtos (organizacao_id, id),
  FOREIGN KEY (organizacao_id, grupo_id) REFERENCES grupos_opcao (organizacao_id, id)
);

CREATE TABLE item_opcoes (
  organizacao_id uuid   NOT NULL REFERENCES organizacoes (id),
  item_id        uuid   NOT NULL,
  opcao_id       uuid   NOT NULL,
  grupo_id       uuid   NOT NULL,
  nome           text   NOT NULL CHECK (length(nome) BETWEEN 1 AND 80),
  preco_centavos bigint NOT NULL CHECK (preco_centavos >= 0),
  PRIMARY KEY (organizacao_id, item_id, opcao_id),
  FOREIGN KEY (organizacao_id, item_id) REFERENCES itens_comanda (organizacao_id, id),
  FOREIGN KEY (organizacao_id, opcao_id) REFERENCES opcoes (organizacao_id, id),
  FOREIGN KEY (organizacao_id, grupo_id) REFERENCES grupos_opcao (organizacao_id, id)
);

CREATE TABLE opcoes_frequencia (
  organizacao_id uuid   NOT NULL REFERENCES organizacoes (id),
  unidade_id     uuid   NOT NULL,
  produto_id     uuid   NOT NULL,
  opcao_id       uuid   NOT NULL,
  lancamentos    bigint NOT NULL CHECK (lancamentos >= 1),
  PRIMARY KEY (organizacao_id, unidade_id, produto_id, opcao_id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, produto_id) REFERENCES produtos (organizacao_id, id),
  FOREIGN KEY (organizacao_id, opcao_id) REFERENCES opcoes (organizacao_id, id)
);

ALTER TABLE grupos_opcao ENABLE ROW LEVEL SECURITY;
ALTER TABLE grupos_opcao FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON grupos_opcao
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

ALTER TABLE opcoes ENABLE ROW LEVEL SECURITY;
ALTER TABLE opcoes FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON opcoes
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

ALTER TABLE produto_grupos_opcao ENABLE ROW LEVEL SECURITY;
ALTER TABLE produto_grupos_opcao FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON produto_grupos_opcao
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

ALTER TABLE item_opcoes ENABLE ROW LEVEL SECURITY;
ALTER TABLE item_opcoes FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON item_opcoes
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

ALTER TABLE opcoes_frequencia ENABLE ROW LEVEL SECURITY;
ALTER TABLE opcoes_frequencia FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON opcoes_frequencia
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
