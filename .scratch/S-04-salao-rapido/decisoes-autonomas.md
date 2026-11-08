# Decisões autônomas (execução noturna)

| Data | Ticket | Dúvida | Escolha | Alternativa descartada |
|---|---|---|---|---|
| 2026-11-08 | todos | Paralelismo (max_parallel 2) vs. árvore única | Execução sequencial na árvore atual (concorrência 1 é permitida); evita conflito de portas/Docker/Testcontainers e de contrato OpenAPI entre tickets | Worktrees paralelos |
| 2026-11-08 | todos | Explorer antes do implementer | Pulado: os packets já trazem spec, seams e comandos; o implementer lê o código | Explorer haiku por ticket |
| 2026-11-08 | F4-0071 | Premissa do ticket (seed falha) | Verificado: só avisava, saía 0; corrigido o aviso no estágio e2e-seed | Desabilitar GSS na connection string |
| 2026-11-08 | F4-0045 | vercel.json destino da API em produção | Mantido staging; registrado em pendencias.md (decisão do dono) | Chutar um domínio de produção |
