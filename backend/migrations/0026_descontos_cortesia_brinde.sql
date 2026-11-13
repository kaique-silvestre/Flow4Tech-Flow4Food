-- Comandas completas II (F4-0058): courtesy, gift and discounts.
--   itens_comanda.tipo          'normal' (charged), 'cortesia' or 'brinde' (charge zero). The frozen price and options stay on the row (the original value for reports and the future stock
--                               write-off); the line total is zero while tipo <> 'normal'. motivo_tipo, tipo_por and tipo_em say why, who and when (present exactly when tipo <> 'normal').
--   descontos_comanda           one row per discount, written once (the app credential has only SELECT and INSERT; a correction is a later ticket). item_id is null for a discount on the whole comanda.
--                               forma is 'percentual' (percentual set) or 'valor' (valor_centavos set); aplicado_centavos is the amount in cents the server computed when it was given and is what the bill uses
--                               (clamped at calculation time so the total is never negative). Everything is tenant data: exact RLS, composite FKs (a discount of another organization or unit is refused).
ALTER TABLE itens_comanda ADD COLUMN tipo text NOT NULL DEFAULT 'normal' CHECK (tipo IN ('normal', 'cortesia', 'brinde'));
ALTER TABLE itens_comanda ADD COLUMN motivo_tipo text CHECK (length(motivo_tipo) BETWEEN 1 AND 200);
ALTER TABLE itens_comanda ADD COLUMN tipo_por uuid;
ALTER TABLE itens_comanda ADD COLUMN tipo_em timestamptz;
ALTER TABLE itens_comanda ADD FOREIGN KEY (organizacao_id, tipo_por) REFERENCES contas (organizacao_id, id);
ALTER TABLE itens_comanda ADD CONSTRAINT itens_tipo_completo
  CHECK ((tipo <> 'normal') = (motivo_tipo IS NOT NULL AND tipo_por IS NOT NULL AND tipo_em IS NOT NULL));

ALTER TABLE itens_comanda ADD CONSTRAINT itens_comanda_organizacao_comanda_id_key UNIQUE (organizacao_id, comanda_id, id);

CREATE TABLE descontos_comanda (
  id                uuid PRIMARY KEY,
  organizacao_id    uuid        NOT NULL REFERENCES organizacoes (id),
  unidade_id        uuid        NOT NULL,
  comanda_id        uuid        NOT NULL,
  item_id           uuid,
  forma             text        NOT NULL CHECK (forma IN ('percentual', 'valor')),
  percentual        numeric(5, 2) CHECK (percentual > 0 AND percentual <= 100),
  valor_centavos    bigint      CHECK (valor_centavos >= 1),
  aplicado_centavos bigint      NOT NULL CHECK (aplicado_centavos >= 1),
  motivo            text        NOT NULL CHECK (length(motivo) BETWEEN 1 AND 200),
  criado_por        uuid        NOT NULL,
  criado_em         timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  CHECK ((forma = 'percentual') = (percentual IS NOT NULL)),
  CHECK ((forma = 'valor') = (valor_centavos IS NOT NULL)),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, comanda_id) REFERENCES comandas (organizacao_id, id),
  FOREIGN KEY (organizacao_id, comanda_id, item_id) REFERENCES itens_comanda (organizacao_id, comanda_id, id),
  FOREIGN KEY (organizacao_id, criado_por) REFERENCES contas (organizacao_id, id)
);

CREATE INDEX descontos_por_comanda ON descontos_comanda (organizacao_id, comanda_id, criado_em, id);

ALTER TABLE descontos_comanda ENABLE ROW LEVEL SECURITY;
ALTER TABLE descontos_comanda FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON descontos_comanda
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON descontos_comanda FROM flow4tech_app;
REVOKE ALL ON descontos_comanda FROM PUBLIC;
