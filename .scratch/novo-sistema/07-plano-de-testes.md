# Novo sistema — Plano de testes e dados de exemplo

Status: rascunho 1. Detalha a estratégia de `04` A9 em algo executável. Requisitos de origem: RNF7 (`02b`), S5 e S18 (`03`), D6 (`01`).
Regra de ouro: **o teste que prova o isolamento entre organizações é o mais importante do sistema.** Um vazamento entre clientes encerra o produto.

## T1. Camadas e o que cada uma cobre

| Camada | Ferramenta | Banco | Cobre | Roda em |
|---|---|---|---|---|
| Unidade de domínio | xUnit + FsCheck (propriedades) | nenhum | Dinheiro, divisão de conta, rateio, custo médio, estoque, arredondamento | Todo commit |
| Integração por módulo | xUnit + Testcontainers | PostgreSQL real, RLS ativo, usuário da aplicação sem bypass | Casos de uso, transações, outbox, migrações, SQL | Todo commit |
| Isolamento entre organizações | Gerado a partir do OpenAPI (T2) | PostgreSQL real | Rotas, SQL, eventos, arquivos, jobs | Todo commit |
| Matriz de permissões | Gerado a partir de `02d` (T3) | PostgreSQL real | Perfil × permissão × escopo × limite | Todo commit |
| Arquitetura | NetArchTest/ArchUnitNET | nenhum | Fronteiras de módulo, rota sem permissão, tabela sem RLS, `float` para dinheiro | Todo commit |
| Contrato de API | Schemathesis (ou equivalente) sobre o OpenAPI | PostgreSQL real | Respostas conforme esquema, erros padronizados | Todo commit |
| Ponta a ponta | Playwright (celular e web) | `staging` efêmero | Fluxos críticos (T6) | Merge no ramo principal e noturno |
| Carga | k6 | Banco de volume (T4, S2) | Metas de desempenho e escala (RNF1) | Semanal e antes de cada fase |
| Migração | xUnit + dump anonimizado do legado | PostgreSQL real | Importador, conferência (`08`) | Todo commit que toca o importador |
| Segurança | SAST, dependências, segredos, ZAP no `staging` | — | S11, S18 | Todo commit / semanal |

Orçamento de tempo do CI: unidade + integração + isolamento + matriz + arquitetura abaixo de **10 min** no PR (paralelizado). O que passar disso vai para o noturno, nunca para "depois".

## T2. Teste de isolamento entre organizações (o desenho)

### T2.1 Preparação: dois tenants com dados ricos
Cada teste de isolamento roda sobre **dois tenants gerados pela mesma semente (S3 abaixo)**, **A** e **B**, com a mesma forma de dados (mesmos nomes de cardápio, mesmas quantidades), mas **identificadores e valores marcados**: cada registro de B carrega um marcador (`B-` no nome, valores terminando em `.77`). O teste procura o marcador de B em tudo que A recebe.
Dois usuários por tenant: um com todas as permissões e um de perfil restrito. Um **superusuário de outro domínio** (Control Tower) para o caso T2.2-i.

### T2.2 O que se verifica (cada item é um teste gerado, não escrito à mão)
| # | Caso | Esperado |
|---|---|---|
| a | **Listagens:** toda rota `GET` de coleção chamada por A | Nenhum registro de B; contagem igual ao esperado de A |
| b | **Leitura por id:** A pede o id de um objeto de B | `404` (nunca `403`: não revelar existência) |
| c | **Escrita por id:** A edita, cancela, exclui ou executa ação sobre objeto de B | `404` e **nada mudou** (comparar o registro de B antes e depois) |
| d | **Referência cruzada:** A cria uma comanda com `produto_id` de B, ou pagamento com método de B | Rejeitado; nada gravado |
| e | **Banco, sem contexto:** conectar como usuário da aplicação e consultar toda tabela de negócio sem definir a organização | 0 linhas; nenhuma exceção silenciosa |
| f | **Banco, com contexto de A:** consultar toda tabela | Nenhuma linha de B; o teste varre `information_schema`, então uma tabela nova entra sozinha |
| g | **Banco, escrita:** com contexto de A, `INSERT`/`UPDATE` apontando `organizacao_id` de B | Rejeitado pela política (`WITH CHECK`) |
| h | **Tabela sem RLS:** teste de arquitetura | Falha se existir tabela com `organizacao_id` sem RLS forçado, ou sem a coluna e sem lista de exceções justificada |
| i | **Control Tower:** token de super admin usado em rota do app e token do app usado em rota da Control Tower | Rejeitado pela audiência do token |
| j | **Eventos e outbox:** A gera evento; verificar consumidores | Handler roda com contexto de A; nenhuma notificação, KDS ou agregado de B |
| k | **Tempo real (SSE):** A assina o fluxo; B gera eventos | A nunca recebe eventos de B; reassinatura com `Last-Event-ID` de B é rejeitada |
| l | **Jobs e worker:** cada tipo de job executa com o contexto da organização do job | Falha se houver acesso sem contexto |
| m | **Arquivos:** chaves de armazenamento incluem a organização; A tenta ler o caminho de um anexo de B | Negado; URL assinada de B não abre para A |
| n | **Importação e exportação:** A importa planilha com id de B; A exporta | Importação rejeita; exportação só traz A |
| o | **Cache e idempotência:** chave de idempotência e cache incluem a organização | Mesma chave em A e B não colide |
| p | **Relatórios e agregados:** totais de A | Nunca incluem B (comparar com soma calculada à mão no banco) |
| q | **Busca (paleta, listas):** termo que só existe em B | Zero resultados para A |
| r | **Mensagens de erro e logs:** erro de A | Sem dado de B (varrer resposta e log pelo marcador `B-`) |

### T2.3 Garantia de cobertura (o teste que testa o teste)
- Toda rota do OpenAPI **precisa declarar** `escopo: organizacao` ou `escopo: global` (com justificativa e revisão). O CI **falha** se a rota não declarar, e falha se uma rota `organizacao` não tiver os casos a–d gerados.
- O gerador lê o OpenAPI: para cada rota monta a chamada de A com o id de B (descoberto por tipo de recurso). O relatório do CI lista **rotas cobertas / total**; o portão S18 exige 100%.
- Mutação deliberada: um teste de "sanidade" **desliga a política RLS de uma tabela em um banco descartável** e confirma que a suíte de isolamento **fica vermelha**. Se ela ficar verde, o gerador está cego.
- A conexão da aplicação em todos os testes de integração **não é dona das tabelas** e **não tem `BYPASSRLS`** (o teste de arranque verifica isso).

## T3. Matriz de permissões
- Fonte: `02d-catalogo-permissoes.md`. Um conversor gera um arquivo versionado (`matriz.json`) com perfil × permissão × limite × escopo.
- Para cada célula: chamar a rota/ação como o perfil e verificar permitido, negado ou restrito (com limite, ex.: desconto acima do limite).
- Casos fixos: **deny vence** conceder; ninguém altera as próprias permissões; dono não é rebaixado nem desativado; último dono; escopo por unidade (garçom da unidade X não vê a Y); sessão revogada após mudança de permissão cai na hora; contas operacionais nunca recebem administração, financeiro ou exportação.
- Qualquer mudança no catálogo sem atualização da matriz gerada falha o CI.

## T4. Dados de exemplo (seeds)
Tudo **sintético, determinístico e versionado**. Nenhum dado de cliente real. Semente fixa, relógio injetável, identificadores determinísticos em teste.

| Nível | Conteúdo | Uso |
|---|---|---|
| **S0 Mínimo** | Uma organização, uma unidade, dono, 3 produtos, 1 insumo, 1 método de pagamento | Testes de unidade e integração por módulo; construído por *builders* em código, sem arquivo |
| **S1 Demonstração "Bar do Zé"** | Espelha o protótipo: organização *Bar do Zé*, unidades *Centro* e *Praia*, dono *Zé Almeida*, gerente *Marina Rocha*, garçons, caixa, cozinha, perfis de fábrica, cardápio com categorias, fichas técnicas, fornecedores, comandas abertas e fechadas, caixa aberto | `staging`, demonstração ao cliente, E2E |
| **S2 Volume** | 3 organizações, 12 meses, até 1.500 comandas/dia na maior, 50 mil insumos movimentados, picos de horário | Carga (k6), partições, relatórios, arquivamento |
| **S3 Isolamento** | Duas organizações com a mesma forma de dados e marcadores (T2.1), usuários por perfil | Suíte de isolamento e matriz |
| **S4 Legado anonimizado** | Cópia do banco atual com nomes, telefones, e-mails e documentos embaralhados | Ensaio de migração (`08`) |

Regras:
- *Builders* (padrão "construtor de objetos de teste") para o código; arquivo de seed só para S1 a S4. Cada módulo entrega **seu builder** junto com o módulo.
- O seed S1 é a **mesma fonte de verdade do protótipo** (mesmos nomes e números) para a conversa com o cliente não mudar de personagens.
- Seeds de produção/`staging` rodam por comando explícito e **recusam rodar** se detectarem organização real (marca `real=true`).
- Dados de S2 são gerados por distribuição realista (curva de horário, ticket médio, taxa de cancelamento) para a carga não ser otimista.

## T5. Testes de propriedade (dinheiro e estoque)
Invariantes que valem para **qualquer** entrada gerada:
- Soma dos pagamentos de uma comanda fechada = total − desconto (ao centavo).
- Divisão de conta: a soma das partes = total, sem perder nem criar centavo; ordem das partes não muda o total.
- Estorno desfaz exatamente o efeito (caixa, estoque, comissão).
- Saldo de estoque = soma dos movimentos; `saldo_apos` de cada movimento bate com o acumulado; nunca negativo sem permissão.
- Custo médio ponderado consistente após qualquer sequência de compras e perdas.
- Comissão = regra × base, arredondada de forma estável.
- Operação idempotente: repetir a mesma chave de idempotência não duplica pagamento nem baixa de estoque.
- Concorrência: dois dispositivos lançando na mesma comanda não perdem item (versão otimista).

## T6. Fluxos ponta a ponta críticos (Playwright)
1. Entrar, abrir comanda e lançar 5 itens em ≤ 2 toques por item.
2. Dois aparelhos na mesma comanda; um lança, o outro vê em tempo real; conflito resolvido sem perda.
3. Dividir a conta e pagar com dois métodos; fechar; caixa e estoque atualizam.
4. Cancelar item com motivo e permissão; estorno.
5. Abertura e fechamento do caixa com diferença.
6. Compra com entrada no estoque e conta a pagar.
7. Usuário novo: credencial temporária mostrada uma vez, troca obrigatória no primeiro acesso.
8. Perfil restrito não vê o que não pode (menu, rota, API).
Orçamento de desempenho no E2E: celular de entrada simulado (CPU 4× mais lenta, rede 4G), meta de lançamento de item e de abertura de comanda do `02b`.

## T7. Carga e desempenho (k6)
Cenários: salão no pico (N garçons × comandas), fechamento simultâneo do turno, relatório longo durante o pico (não pode degradar o lançamento), importação grande no worker. Passa se p95 dentro da meta do `02b`, sem erro e sem crescimento de fila. A **latência de rede** até o Railway foi dada como validada (ver `04` A10); a carga mede o servidor, não a rede.

## T8. Testes de migração e do importador
Detalhados em `08`. Em resumo: o importador roda sobre S4 (legado anonimizado) em `staging`, **várias vezes** (idempotente), e a conferência (contagens, somas de estoque, saldos em aberto) é automática e vira relatório.

## T9. Política de qualidade
- **Teste instável (flaky)** é defeito: quarentena em até 1 dia, correção em até 1 semana, nunca "re-rodar até passar".
- Cobertura não é meta isolada. Metas: **100% das rotas no isolamento**, **100% das células da matriz**, **mutação** (Stryker.NET) no domínio de dinheiro e estoque com escore mínimo combinado antes da Fase 2.
- Bug de produção vira teste que falha **antes** da correção.
- Todo ADR de segurança aponta o teste que o protege.

## T10. Como entra no roadmap
| Quando | Entrega | Onde |
|---|---|---|
| Fase 0 | Esqueleto das suítes, builders S0, CI com portões, mutação sanidade do RLS | 0.2, 0.6 |
| Fase 1 | Seed S3, gerador de isolamento (T2) e conversor da matriz (T3), arquitetura | 1.2, 1.6 |
| Fase 2 | S1 (Bar do Zé), E2E dos fluxos 1 a 5, propriedades de dinheiro | 2.x |
| Fase 3 | Propriedades de estoque, fluxo 6 | 3.x |
| Fase 5 | S2 e carga, S4 e ensaios de migração, portão S18 | 5.1, 5.3, 5.4 |

## T11. Em aberto
1. Gerador de isolamento: feito sob medida sobre o OpenAPI ou adaptar Schemathesis com ganchos? Spike curto na Fase 0.
2. Ferramenta de mutação para .NET (Stryker.NET) vale o tempo de CI? Rodar noturno.
3. Anonimização do legado (S4): quem executa e como garantir que nenhum dado real sai do banco atual (ver `08` M6).
