-- F4-0139: the comanda stream reads the outbox of one organization by time (ocorrido_em >= window start, ordered by ocorrido_em, id), delivered or not.
-- The existing index (0002) only covers pending rows, so this one serves the stream's read.
CREATE INDEX outbox_eventos_por_tempo ON outbox_eventos (organizacao_id, ocorrido_em, id);
