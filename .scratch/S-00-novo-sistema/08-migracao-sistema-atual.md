# Novo sistema — Migração do sistema atual

Status: rascunho 1, **análise do sistema atual (branch `main`) e proposta para decisão**. Origem: D8 (`01`), F17 (`02`), Fase 5 (`06`, itens 5.1 a 5.6).
Fonte da análise: modelos, migrações e rotas do back-end atual em `main` (Python/FastAPI, SQLAlchemy, Alembic, PostgreSQL), 2026-10-31. **Não vi dados reais**, só o esquema. O que depende do volume e da qualidade dos dados está em M8.

## M1. O que o sistema atual é (resumo técnico)
- Back-end Python (FastAPI + SQLAlchemy 2 + Alembic), PostgreSQL no Railway, front React. 50 migrações, ~174 rotas, ~38 modelos.
- **Já é multi-tenant** (`tenant_id` inteiro em toda tabela, RLS adicionado nas migrações 0042 a 0050, usuário de aplicação separado).
- Identificadores **inteiros sequenciais**. Dinheiro em `Numeric` com 2 casas (preço, pagamento, caixa) e estoque com 4 casas.
- Senhas com **bcrypt**; autenticação por JWT com refresh e revogação.
- Existe rota de backup (`/backup`) e script de criação de cliente (`scripts/setup_cliente.py`).
- O histórico das migrações mostra **limpezas de dados operacionais** (0020 apaga tabelas operacionais, 0022 corrige unidades legadas, 0023 zera `quantidade_caixa`). Indício de que os dados de teste e de produção podem estar misturados: **confirmar antes de importar qualquer coisa** (M8).

## M1b. Dados reais encontrados (consulta somente leitura, Railway `production`, 2026-10-31)
Banco de **12 MB** no total, 4 tenants (consulta feita só com `SELECT` em sessão somente leitura; nada foi alterado).

| Tenant | Nome | Conteúdo | Conclusão |
|---|---|---|---|
| 1 | Tenant Padrão | 3 comandas abertas, 50 produtos, 1 usuário (sem e-mail) | Padrão do sistema, **descartar** |
| 2 | Matchpoint | 16 comandas em 29/05 (6 abertas, 5 canceladas, 5 fechadas, R$ 141), 3 insumos | Teste de maio, **descartar** |
| 3 | Flow4Tech | só usuário admin, sem dados | Administração da casa, **descartar** |
| **4** | **Matchpoint** | **17 comandas (14 fechadas, 3 canceladas, R$ 1.420,00 em pagamentos) em 6 dias entre 29/05 e 09/10, 59 itens vendidos, 58 produtos, 51 insumos, 39 fichas técnicas, 178 movimentos de estoque, 29 compras, 4 fornecedores, 1 garçom, 4 métodos de pagamento, 1 usuário (último login 09/10)** | **É o restaurante real** (confirma "um restaurante") |

Outras observações:
- **Zero comandas abertas** no tenant 4. Caixa **nunca usado** (0 sessões), contas a pagar **vazias**, comissões **vazias**, promoções vazias.
- **Estoque consistente:** nenhum insumo negativo e todos os `estoque_atual` batem com o `saldo_apos` do último movimento. Todos os produtos têm preço.
- **Assinaturas:** 2, ambas em `trial`, sem pagamentos. Não há cobrança real a migrar.
- Mais acessos recentes: o usuário do tenant 2 (login em 27/10) e o do tenant 4 (09/10). Confirmar qual deles o dono usa hoje, pois o tenant 2 parece de teste.
- Só existe **um usuário por tenant**: não há equipe cadastrada, então M4 (perfis e credenciais) praticamente some.

**Efeito na análise:** o volume é minúsculo (cabe em uma planilha) e o histórico real são **6 dias de venda**. A Fase 5 não é uma migração pesada; é praticamente **recarregar cadastros** (cardápio, insumos, fichas, fornecedores, compras recentes) e **contar o estoque**.

## M2. Mapa de dados: atual → novo

| Atual (tabela) | Campos relevantes | Novo | Tratamento |
|---|---|---|---|
| `tenants` | nome fantasia, CNPJ, status | Organização | Um registro por cliente real; descartar tenants de teste |
| `estabelecimento` | nome, CNPJ, endereço, telefone | Unidade (dados da casa) | Criar **uma unidade** (o atual não tem multiunidade) |
| `categorias` | nome (**único global**), `parent_id`, ativo | Categorias | Direto; a unicidade global some, conferir duplicatas por nome entre tenants |
| `insumos` | nome, categoria, `unidade_base` (un, g, kg), `custo_medio`, `estoque_atual`, `estoque_reservado`, `nivel_critico`, ativo | Insumos | Direto; `quantidade_caixa` descartada (já zerada pela 0023); reservado é **calculado** no novo, não migrar |
| `produtos` | nome, categoria, `preco_venda` (opcional), ativo | Produtos do cardápio | Direto; preço nulo vira "sem preço" e vai para a lista de pendências |
| `ficha_tecnica` | produto, insumo, quantidade (4 casas) | Fichas técnicas | Direto; validar referências e unidade |
| `fornecedores` | nome, telefone, e-mail, ativo | Fornecedores | Direto |
| `metodos_pagamento` | nome, tipo, padrão, ativo | Métodos de pagamento | Mapear `tipo` para os tipos do novo; manter nomes |
| `garcons` | nome, ativo | **Colaboradores** (função garçom) | O atual separa garçom de usuário; no novo é uma pessoa só. Casar por nome com `system_users` e **revisar manualmente** |
| `system_users`, `profiles`, `profile_permissions` | usuário, e-mail opcional, perfil, permissão **por tela** (12 telas) | Usuários, perfis e permissões granulares | Ver M4 |
| `comandas`, `itens_comanda`, `pagamentos`, `eventos_comanda`, `comissoes_garcom` | histórico de vendas | Comandas, pagamentos, comissões | **Não migrar para as tabelas operacionais** (ver M3, opção B) |
| `movimentos_estoque` | tipo, quantidade, `saldo_apos`, motivo, compra | Movimentos de estoque | Não migrar o histórico; gerar **um movimento de "saldo inicial"** por insumo (ver M5) |
| `compras`, `itens_compra` | fornecedor, data, nota, status, previsões | Compras | Migrar só **compras em aberto** (agendadas e a receber). Histórico fica no arquivo (M3) |
| `contas_pagar` | valor, vencimento, status | Contas a pagar | Migrar as **pendentes** como saldos em aberto |
| `caixa_sessoes`, `caixa_movimentos` | sessões e sangrias | Caixa | **Fechar o caixa** antes da virada; não migrar |
| `notificacoes` | tipo, mensagem, lida | Notificações | Descartar |
| `assinaturas`, `billing*`, `config_seguranca`, `revoked_tokens`, `refresh_tokens`, `password_resets` | cobrança e sessão | Control Tower / sessões | Sessões e tokens: descartar. Assinatura e cobrança: ver M8 (há clientes cobrados hoje?) |

## M3. Decisão central: o que fazer com o histórico de vendas
O dono precisa escolher. Três opções:

| Opção | O que significa | Esforço | Risco | Efeito para o dono |
|---|---|---|---|---|
| **A. Começar do zero** | Só cadastros e saldos. Histórico fica só no arquivo do sistema antigo | Baixo | Baixo | Relatórios e comparativos com o passado só no sistema antigo |
| **B. Cadastros e saldos + histórico resumido (recomendada)** | A mais: **totais por dia, por produto, por método e por garçom** dos últimos 12 a 24 meses, importados como agregados (não como comandas) | Médio | Baixo | Gráficos e comparativos "mesmo dia do ano passado" funcionam desde o primeiro dia |
| **C. Migração total** | Todas as comandas, itens e pagamentos entram como se tivessem sido feitos no novo | Alto | **Alto** | Histórico completo no mesmo lugar, ao custo de forçar os dados antigos nas regras novas |

**Recomendação atualizada após ver os dados (M1b): A.** Com 14 comandas fechadas em 6 dias, agregados históricos não têm valor para o dono, e o arquivo do sistema antigo guarda tudo. Se mesmo assim quiser o passado visível, o custo de B ou até C é desprezível nesse volume, mas as razões abaixo contra C continuam valendo. Motivos contra C (e a favor de B quando houver histórico maior):
1. As regras do novo são mais rígidas (registros imutáveis, estorno por lançamento, divisão de conta, auditoria encadeada). **Comandas antigas não satisfazem esses invariantes**: o atual permite `reaberta`, valores sem estorno encadeado, `desconto` sem permissão registrada, total e saldo anuláveis. Importá-las como verdade criaria dados que o novo sistema nunca produziria e que o teste de propriedades (`07` T5) rejeitaria.
2. O histórico que o dono realmente usa é **agregado** (vendas por dia, produtos mais vendidos, CMV). Isso importa sem risco.
3. A auditoria do novo é **encadeada por hash a partir do primeiro evento**. Inserir passado nela estragaria a prova de integridade.
4. O sistema antigo fica **somente leitura** como arquivo de consulta e em backup após o desligamento (já previsto no critério de saída da Fase 5).
Se o dono exigir C, deve ser feito em uma tabela de **arquivo histórico separada** (somente leitura, marcada como "migrada"), nunca nas tabelas operacionais.

## M4. Usuários e acesso
- **Senhas:** o atual usa bcrypt; o novo, Argon2id (S6). Opções: (1) **não migrar hashes** e emitir credencial temporária para cada pessoa, com troca obrigatória no primeiro acesso, como manda a D13; (2) migrar hashes bcrypt e reprocessá-los no primeiro login. **Recomendação: opção 1.** É mais simples, não herda hashes antigos e já é o fluxo padrão do novo. Custo: o dono entrega credenciais à equipe no dia da virada (equipe pequena, um evento só).
- **Perfis:** o atual usa permissão por **tela** (12 telas). Mapear cada perfil existente para o perfil de fábrica mais próximo (`02d`) e **revisar manualmente** os perfis personalizados, em uma tabela "perfil antigo → perfil novo → permissões extras". Nunca conceder mais do que o perfil antigo permitia.
- **Garçons sem usuário** (só existiam como nome na comanda) viram colaboradores **sem acesso ao sistema**, que o novo já suporta.
- **Usuário único por tenant** (nome de usuário) continua valendo; e-mail opcional continua (conta operacional sem e-mail).
- Conta do dono: a pessoa é o **dono** da nova organização (proteções do dono, F11).

## M5. Saldos que precisam fechar na virada
| Saldo | Como migra | Conferência |
|---|---|---|
| Estoque por insumo | Um movimento "saldo inicial" por insumo, com quantidade de `estoque_atual` e custo de `custo_medio` | Soma de `quantidade × custo` antes e depois; `saldo_apos` do último movimento = `estoque_atual` (acha dados inconsistentes) |
| Contas a pagar pendentes | Registro com valor, vencimento e fornecedor | Soma e contagem por vencimento |
| Compras agendadas / a receber | Pedido em aberto | Contagem e total |
| Comandas abertas | **Não migrar.** Fechar ou cancelar todas antes da virada (janela fora do horário de pico) | Zero comandas abertas no antigo |
| Caixa | **Fechar a sessão** e conferir | Nenhuma sessão aberta |
| Comissões de garçom não pagas | Lançamento de saldo a pagar por colaborador | Soma por garçom |
| Fiado / crédito de clientes | Só se o atual tiver (não vi no esquema atual); ver R16-2 (`06`) | — |

## M6. Como executar (proposta)
1. **Extrator de uma vez só** (script descartável, fora do produto) lê o banco antigo **em modo somente leitura** a partir de um backup restaurado, nunca do banco de produção em uso.
2. Ele produz **as próprias planilhas do modelo F17** (produtos, insumos, fichas, fornecedores, métodos, saldos iniciais), mais os arquivos de pendências e de histórico agregado.
3. A carga usa **o importador do produto** (F17: pré-visualização, validação, relatório de erros, desfazer). Assim o caminho de migração é o mesmo que um cliente novo usará, testado e auditado.
4. **Tabela de correspondência de identificadores** (`legado_id` → UUID novo) guardada na organização, para reconferir e para o histórico agregado referenciar produtos.
5. **Ensaios repetidos em `staging`** sobre S4 (`07`): rodar, conferir, apagar a organização, rodar de novo. A carga final só acontece quando um ensaio passa sem intervenção manual.
6. **Relatório de conferência automático**: contagens por entidade, somas de estoque e de saldos em aberto, lista de pendências (produto sem preço, insumo sem unidade, categoria duplicada), com aprovação do dono.
7. **Anonimização para os ensaios (S4):** executar **dentro** do ambiente do banco atual, antes de exportar, trocando nomes, telefones, e-mails e documentos. Nenhum dado real sai do ambiente atual para ensaio.
8. **LGPD:** dados pessoais migrados são só os de colaboradores e fornecedores; manter base legal e retenção definidas em `03`/`02b`; o backup do sistema antigo guardado com prazo de retenção.

## M7. Virada, paralelo e reversão (detalha 5.2 e 5.6)
- **Antes (D−7 a D−1):** congelar cadastros no antigo; ensaio final em `staging`; conferência assinada pelo dono; treinamento por perfil.
- **Dia D (fora do horário de venda):** fechar comandas e caixa no antigo; backup final; carga; conferência; liberar credenciais; abrir caixa no novo.
- **Paralelo curto:** o antigo fica **somente leitura**, para consulta e conferência, não para lançamento duplicado.
- **Reversão:** definir a janela (ex.: primeiros 3 dias). Voltar ao antigo significa reabrir o antigo e **digitar manualmente** as vendas feitas no novo (exportar comandas e pagamentos do período em planilha para isso). A decisão de voltar tem dono e critério (`06` 5.6).
- **Depois:** antigo desligado com backup completo criptografado e retenção combinada.

## M8. Perguntas ao dono (bloqueiam o plano)
1. ~~Quantos restaurantes?~~ **Respondido: um** (tenant 4, confirmado nos dados). **Respondido pelo dono (2026-11-01):** os tenants 1, 2 e 3 não migram; os dados ficam apenas guardados no sistema antigo/backup.
2. ~~Quanto histórico?~~ **Respondido pelos dados:** 17 comandas em 6 dias (M1b).
3. ~~Opção A, B ou C?~~ **Decidido pelo dono (2026-11-01): opção B** (cadastros, saldos e histórico resumido em agregados; M3). Obs.: com o volume atual, o resumo é quase vazio; o mecanismo de agregados importados continua valendo para quando houver histórico maior.
4. ~~Clientes, mesas ou fiado?~~ **Respondido pelo dono (2026-11-01): não usa.** Nada a migrar desses itens.
5. **Quantas pessoas** têm acesso e quantos perfis personalizados existem? **Em aberto** (dono não sabe ainda). Bloqueia só a revisão de M4, não as fases 0 a 4.
6. ~~Estoque físico conferido?~~ **Adiado pelo dono (2026-11-01):** transição não é preocupação agora. Retomar antes da Fase 5; a recomendação da contagem no dia anterior continua valendo.
7. ~~Assinatura/cobrança ativa?~~ **Não:** 2 assinaturas em trial, sem pagamentos. Nada a migrar.

## M9. Riscos
| Risco | Mitigação |
|---|---|
| Saldo de estoque do sistema atual não bate com o físico | Contagem física antes da virada; relatório de inconsistência (`saldo_apos` × `estoque_atual`) |
| Dados de teste misturados com os reais | Triagem no extrator (por tenant e por data); aprovação do dono |
| Perfil antigo com permissão que o novo não tem | Tabela de equivalência revisada; nunca ampliar acesso |
| Virada interrompida no meio do dia | Janela fora do pico e roteiro com ponto de não retorno claro |
| Dado pessoal vazando nos ensaios | Anonimização dentro do ambiente atual (M6.7) |
| Dono quer migração total depois da virada | Arquivo histórico separado (M3, opção C), sem tocar nas tabelas operacionais |

## M10. Impacto no planejamento
- **`01` D8:** continua "fora do foco" para o produto, mas a **migração do restaurante atual é uma entrega da Fase 5** (5.1) com este documento como base. O importador (F17) já entra antes da Fase 5: cardápio na 2.12, insumos e saldos na 3.10, clientes e saldos pendentes na 4.7. Falta incluir no importador **saldo inicial de estoque como movimento**, **contas a pagar pendentes** e **histórico agregado** (opção B).
- Entregas novas sugeridas na Fase 5: 5.1a extrator e ensaios em `staging`; 5.1b conferência e aprovação do dono; 5.1c histórico agregado (se opção B).
- Alimenta `07`: seed S4 e ensaios de migração.
