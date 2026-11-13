-- Metodos de pagamento e recebimento misto (F4-0063).
--   metodos_pagamento   the payment methods of an organization. chave is the stable key of a factory method (dinheiro, credito, debito, pix, vale-refeicao, vale, conta-credito; null for one the
--                       organization created); nome is the organization's to change; tipo 'dinheiro' is only the factory cash (the only one with change and with cash movement in the shift). Factory
--                       rows are created on first use by the application (voucher and credit account inactive until phase 2B). Never deleted (the app credential has no DELETE), only deactivated.
--   pagamentos          stays append-only (SELECT and INSERT). A mixed payment is several rows, one per method line. New columns: metodo_id (null on rows before this migration) and metodo_nome (the name at the
--                       time), gorjeta_centavos (tip: outside the balance, never part of aplicado), estado ('confirmado', or 'pendente' for an asynchronous provider that nothing implements yet: only
--                       confirmed rows count in the balance), divisao_id + divisao_parte (optional link to a part of a recorded split, composite FK to divisao_partes). Change exists only for cash.
CREATE TABLE metodos_pagamento (
  id              uuid PRIMARY KEY,
  organizacao_id  uuid        NOT NULL REFERENCES organizacoes (id),
  chave           text        CHECK (chave IN ('dinheiro', 'credito', 'debito', 'pix', 'vale-refeicao', 'vale', 'conta-credito')),
  nome            text        NOT NULL CHECK (length(nome) BETWEEN 1 AND 40 AND nome = btrim(nome)),
  tipo            text        NOT NULL CHECK (tipo IN ('dinheiro', 'outro')),
  ativo           boolean     NOT NULL,
  ordem           integer     NOT NULL CHECK (ordem >= 1),
  criado_em       timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  CHECK ((tipo = 'dinheiro') = COALESCE(chave = 'dinheiro', false))
);

CREATE UNIQUE INDEX metodos_pagamento_chave ON metodos_pagamento (organizacao_id, chave) WHERE chave IS NOT NULL;
CREATE UNIQUE INDEX metodos_pagamento_nome ON metodos_pagamento (organizacao_id, lower(nome));

ALTER TABLE metodos_pagamento ENABLE ROW LEVEL SECURITY;
ALTER TABLE metodos_pagamento FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON metodos_pagamento
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

REVOKE DELETE, TRUNCATE, REFERENCES, TRIGGER ON metodos_pagamento FROM flow4tech_app;
REVOKE ALL ON metodos_pagamento FROM PUBLIC;

ALTER TABLE pagamentos DROP CONSTRAINT pagamentos_metodo_check;
ALTER TABLE pagamentos ADD CONSTRAINT pagamentos_metodo_check
  CHECK (metodo IN ('dinheiro', 'credito', 'debito', 'pix', 'vale-refeicao', 'vale', 'conta-credito', 'personalizado'));
ALTER TABLE pagamentos
  ADD COLUMN metodo_id        uuid,
  ADD COLUMN metodo_nome      text   NOT NULL DEFAULT 'Dinheiro',
  ADD COLUMN gorjeta_centavos bigint NOT NULL DEFAULT 0 CHECK (gorjeta_centavos >= 0),
  ADD COLUMN estado           text   NOT NULL DEFAULT 'confirmado' CHECK (estado IN ('confirmado', 'pendente')),
  ADD COLUMN divisao_id       uuid,
  ADD COLUMN divisao_parte    integer;
ALTER TABLE pagamentos ALTER COLUMN metodo_nome DROP DEFAULT;
ALTER TABLE pagamentos ALTER COLUMN gorjeta_centavos DROP DEFAULT;
ALTER TABLE pagamentos ALTER COLUMN estado DROP DEFAULT;
ALTER TABLE pagamentos ADD CONSTRAINT pagamentos_metodo_nome_check CHECK (length(metodo_nome) BETWEEN 1 AND 40);
ALTER TABLE pagamentos ADD CONSTRAINT pagamentos_troco_so_dinheiro CHECK (troco_centavos = 0 OR metodo = 'dinheiro');
ALTER TABLE pagamentos ADD CONSTRAINT pagamentos_divisao_completa CHECK ((divisao_id IS NULL) = (divisao_parte IS NULL));
ALTER TABLE pagamentos ADD FOREIGN KEY (organizacao_id, metodo_id) REFERENCES metodos_pagamento (organizacao_id, id);
ALTER TABLE pagamentos ADD FOREIGN KEY (organizacao_id, divisao_id, divisao_parte) REFERENCES divisao_partes (organizacao_id, divisao_id, numero);

CREATE INDEX pagamentos_por_divisao ON pagamentos (organizacao_id, divisao_id, divisao_parte) WHERE divisao_id IS NOT NULL;
