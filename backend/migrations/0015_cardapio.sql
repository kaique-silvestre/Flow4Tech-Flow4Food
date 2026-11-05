-- Dominio do salao, ticket 02: minimal menu. Categories and products of an organization (tenant tables, exact RLS, composite FKs).
--   produtos.codigo        short code, stored upper case, unique per organization.
--   produtos.preco_centavos price in integer cents (never float/decimal); moeda is BRL for now.
--   produtos.versao        grows on every change (optimistic concurrency of the editor; comanda items freeze name and price when launched, so a change never reaches them).
--   produtos_frequencia    how many times a product was launched in a unit (feeds "frequent items"); empty until comandas launch items.
CREATE TABLE categorias (
  id             uuid PRIMARY KEY,
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  nome           text NOT NULL CHECK (length(nome) BETWEEN 1 AND 80),
  ativa          boolean NOT NULL DEFAULT true,
  versao         integer NOT NULL DEFAULT 1 CHECK (versao >= 1),
  criada_em      timestamptz NOT NULL,
  atualizada_em  timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  UNIQUE (organizacao_id, nome)
);

CREATE TABLE produtos (
  id              uuid PRIMARY KEY,
  organizacao_id  uuid NOT NULL REFERENCES organizacoes (id),
  categoria_id    uuid NOT NULL,
  codigo          text NOT NULL CHECK (codigo ~ '^[A-Z0-9][A-Z0-9-]{0,19}$'),
  nome            text NOT NULL CHECK (length(nome) BETWEEN 1 AND 120),
  preco_centavos  bigint NOT NULL CHECK (preco_centavos >= 0),
  moeda           text NOT NULL DEFAULT 'BRL' CHECK (moeda = 'BRL'),
  ativo           boolean NOT NULL DEFAULT true,
  versao          integer NOT NULL DEFAULT 1 CHECK (versao >= 1),
  criado_em       timestamptz NOT NULL,
  atualizado_em   timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  UNIQUE (organizacao_id, codigo),
  FOREIGN KEY (organizacao_id, categoria_id) REFERENCES categorias (organizacao_id, id)
);

CREATE TABLE produtos_frequencia (
  organizacao_id uuid NOT NULL REFERENCES organizacoes (id),
  unidade_id     uuid NOT NULL,
  produto_id     uuid NOT NULL,
  lancamentos    bigint NOT NULL DEFAULT 0 CHECK (lancamentos >= 0),
  PRIMARY KEY (organizacao_id, unidade_id, produto_id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, produto_id) REFERENCES produtos (organizacao_id, id)
);

ALTER TABLE categorias ENABLE ROW LEVEL SECURITY;
ALTER TABLE categorias FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON categorias
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

ALTER TABLE produtos ENABLE ROW LEVEL SECURITY;
ALTER TABLE produtos FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON produtos
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

ALTER TABLE produtos_frequencia ENABLE ROW LEVEL SECURITY;
ALTER TABLE produtos_frequencia FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON produtos_frequencia
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));
