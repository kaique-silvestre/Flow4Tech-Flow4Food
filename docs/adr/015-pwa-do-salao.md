# 015. PWA do salão: fila de ações e idempotência no cliente

- Status: accepted
- Date: 2026-11-05

## Context
O garçom usa o `/salao` no celular, em rede instável do salão. Perder um lançamento ou lançá-lo duas vezes (rodada duplicada, pagamento duplicado) é pior que esperar. Um modo offline completo (leitura e escrita sem rede, com conflito e fusão) é caro e não foi pedido (decisão D7 da spec do domínio do salão, A11).

## Decision
O app do salão é uma PWA com uma fila de ações no cliente: cada ação de escrita (abrir comanda, lançar rodada, registrar pagamento) vira um item da fila, enviado em ordem, reenviado em caso de falha de rede. Cada ação carrega um id gerado no cliente (UUID v7); o servidor trata esse id como chave de idempotência por organização: repetir o mesmo id devolve o resultado já gravado sem repetir o efeito. Não há offline completo: sem rede o app mostra o estado da fila e dos dados já carregados, mas não calcula nem decide nada pelo servidor (preço, total, autorização continuam do servidor, ADR-006).

## Consequences
Reenvio é seguro e a fila pode ser simples. O servidor precisa guardar o id de cada ação de escrita com unicidade (por organização, sob RLS). Ações que dependem de leitura nova (fechar com total) falham de forma clara sem rede. Offline completo fica como decisão futura, com seu próprio ADR.

## Alternatives considered
- Offline completo com sincronização e resolução de conflitos: custo e risco financeiros altos para o ganho no salão.
- Sem fila, só repetir o pedido manualmente: duplica rodadas e pagamentos quando a resposta se perde.
- Id gerado no servidor: não protege o reenvio quando a resposta se perde.
