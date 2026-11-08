# Decisões autônomas (execução noturna)

| Data | Ticket | Dúvida | Escolha | Alternativa descartada |
|---|---|---|---|---|
| 2026-11-08 | todos | Paralelismo (max_parallel 2) vs. árvore única | Execução sequencial na árvore atual (concorrência 1 é permitida); evita conflito de portas/Docker/Testcontainers e de contrato OpenAPI entre tickets | Worktrees paralelos |
| 2026-11-08 | todos | Explorer antes do implementer | Pulado: os packets já trazem spec, seams e comandos; o implementer lê o código | Explorer haiku por ticket |
| 2026-11-08 | F4-0071 | Premissa do ticket (seed falha) | Verificado: só avisava, saía 0; corrigido o aviso no estágio e2e-seed | Desabilitar GSS na connection string |
| 2026-11-08 | F4-0045 | vercel.json destino da API em produção | Mantido staging; registrado em pendencias.md (decisão do dono) | Chutar um domínio de produção |
| 2026-11-08 | F4-0046 | Gerente pode gerir perfis/atribuir permissões? | Não: modelo gerente sem perfil:gerir/perfil:ver/permissao:atribuir (padrão mais restritivo); dono ajusta em ModelosDePerfil | Dar poder de escalada ao gerente |
| 2026-11-08 | F4-0047 | Falta backend (listar pessoas, criar pessoa, permissões na sessão) | Ampliado o escopo do F4-0047: GET /contas (somente leitura, perfil:ver) + prefixos perfil:/permissao: em /sessao/permissoes, feitos por implementer backend antes da UI. NÃO criar POST /contas (emitir credencial temporária é decisão de segurança do dono) -> criterio "nova pessoa" adiado, vai para pendencias.md | Criar rota de criação de conta com credencial |
| 2026-11-08 | F4-0048 | Gerente altera preço? | Não por padrão (sem limite escolhido); pendência do dono | Conceder sem limite |
| 2026-11-08 | F4-0050 | Preço de adicional sem limite? | Sem limite (produto:gerenciar); registrado como risco de negócio | Exigir cardapio:preco-alterar para adicionais |
| 2026-11-08 | F4-0051 | Toast claro no tema escuro (.f4-toast) e follow-ups (temOpcoes em ProdutoDto; "corrigir e reenviar" na fila) | Adiados, fora do escopo; listados no resumo final | Corrigir tudo aqui |
| 2026-11-08 | F4-0050 | CI vermelho (sast codeql 504 download) | Falha externa, rerun --failed | Alterar workflow |
