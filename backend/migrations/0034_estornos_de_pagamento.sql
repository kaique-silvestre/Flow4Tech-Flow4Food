-- Estorno de pagamento (F4-0145): a refund is a NEGATIVE, immutable entry linked to the original payment; the payment row is never touched.
--   estornos_pagamento                       one row per refund, written once: the app credential has only SELECT and INSERT. valor_centavos is NEGATIVE (the cents handed back). The sum of the
--                                            refunds of a payment never passes its aplicado_centavos (checked by the use case under an advisory lock on the payment). turno_id is the shift the refund
--                                            belongs to (the payment's own while it is open, otherwise the open one of the operator); null only when the unit does not require an open shift.
--   turnos_caixa.estornado_dinheiro_centavos running total of the cash refunded while the shift is open; closing computes esperado = fundo + recebido - estornado + suprimentos - sangrias in SQL.
ALTER TABLE turnos_caixa ADD COLUMN estornado_dinheiro_centavos bigint NOT NULL DEFAULT 0 CHECK (estornado_dinheiro_centavos >= 0);

CREATE TABLE estornos_pagamento (
  id               uuid PRIMARY KEY,
  organizacao_id   uuid        NOT NULL REFERENCES organizacoes (id),
  unidade_id       uuid        NOT NULL,
  pagamento_id     uuid        NOT NULL,
  comanda_id       uuid        NOT NULL,
  turno_id         uuid,
  metodo_id        uuid,
  metodo           text        NOT NULL,
  valor_centavos   bigint      NOT NULL CHECK (valor_centavos <= -1),
  motivo           text        NOT NULL CHECK (length(btrim(motivo)) BETWEEN 1 AND 200),
  operador_id      uuid        NOT NULL,
  registrado_em    timestamptz NOT NULL,
  UNIQUE (organizacao_id, id),
  FOREIGN KEY (organizacao_id, unidade_id) REFERENCES unidades (organizacao_id, id),
  FOREIGN KEY (organizacao_id, pagamento_id) REFERENCES pagamentos (organizacao_id, id),
  FOREIGN KEY (organizacao_id, comanda_id) REFERENCES comandas (organizacao_id, id),
  FOREIGN KEY (organizacao_id, turno_id) REFERENCES turnos_caixa (organizacao_id, id),
  FOREIGN KEY (organizacao_id, operador_id) REFERENCES contas (organizacao_id, id)
);

CREATE INDEX estornos_por_pagamento ON estornos_pagamento (organizacao_id, pagamento_id);
CREATE INDEX estornos_por_comanda ON estornos_pagamento (organizacao_id, comanda_id, registrado_em);
CREATE INDEX estornos_por_turno ON estornos_pagamento (organizacao_id, turno_id) WHERE turno_id IS NOT NULL;

ALTER TABLE estornos_pagamento ENABLE ROW LEVEL SECURITY;
ALTER TABLE estornos_pagamento FORCE  ROW LEVEL SECURITY;
CREATE POLICY isolamento ON estornos_pagamento
  USING (organizacao_id = (SELECT app_organizacao_id()))
  WITH CHECK (organizacao_id = (SELECT app_organizacao_id()));

REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON estornos_pagamento FROM flow4tech_app;
REVOKE ALL ON estornos_pagamento FROM PUBLIC;
