# Novo sistema — Cobertura do sistema atual

Objetivo: garantir que **toda funcionalidade do sistema atual** esteja, no mínimo, **requisitada** no novo (não implica copiar a implementação nem a tela).
Fonte da conferência: rotas da API (174), modelos (38), telas do front (45), documentos de planejamento e PRDs da branch `refactor`, em 2026-10-21.
Legenda: **OK** = já coberto no catálogo; **AMPLIADO** = estava no catálogo de forma genérica e foi detalhado; **ADICIONADO** = faltava e entrou agora.

## 1. Autenticação e contas

| Sistema atual | Novo | Status |
|---|---|---|
| Login, refresh, logout, "quem sou eu" | F11 autenticação e sessões | OK |
| Troca de senha pelo próprio usuário | F11 | OK |
| "Esqueci a senha" por link com token e redefinição | F11 / S6 recuperação de acesso | OK |
| Limite de tentativas no login e na redefinição | S6, S11 | OK |
| Verificar se usuário/e-mail já existe | F11 (unicidade por organização) | OK |
| Usuário com ou sem e-mail, dono (`is_owner`) protegido | F11 (dois tipos de conta, proteções do dono) | OK |
| Ativar/desativar usuário sem apagar histórico | F11 | OK |
| Redefinição de senha por administrador, auditada | F11 `usuario:redefinir_acesso` | OK |
| Último acesso do usuário | F11 / F10 auditoria | AMPLIADO (registrar último acesso e dispositivo) |

## 2. Permissões e perfis

| Sistema atual | Novo | Status |
|---|---|---|
| Permissão por tela (12 telas) | Permissões granulares `recurso:ação` por feature | OK (superado) |
| Perfis com lista de telas, ativos/inativos, de sistema (`is_system`) | F11 perfis customizáveis e de fábrica | OK |
| Perfil com usuários não pode ser excluído/desativado | F11 regras de proteção | AMPLIADO (registrar a regra) |
| Permissões individuais por usuário além do perfil | F11 overrides individuais | OK |
| **Modelos de permissão (templates) que travam edição manual** | F11 modelos; catálogo e perfis padrão em `02d` | ADICIONADO |
| Usuário não altera as próprias permissões | F11 / S8 anti-autopromoção | OK |
| Módulos liberados por organização (feature flags por tenant) | F12.4 | OK |

## 3. Comandas e salão

| Sistema atual | Novo | Status |
|---|---|---|
| Abrir, listar, editar comanda; contador de abertas | F1.1, F1 lista | OK |
| Identificação por nome ou mesa | F1 (identificação) | ADICIONADO |
| Número sequencial diário da comanda | F1 | ADICIONADO |
| Número de pessoas; pessoa associada ao item | F1, F1.11 | ADICIONADO |
| Lançar, editar item; observação por item | F1.2, F1.3 | OK |
| Cancelar item com motivo (cliente desistiu, erro de lançamento, item indisponível, outro) | F1.4 | AMPLIADO (motivos padronizados) |
| Cortesia de item | F1.6 | OK |
| Desconto percentual e em valor, com validação (não acima do subtotal) | F1.8 | OK |
| Taxa de serviço (liga/desliga por estabelecimento) | F1.9, F14 | OK |
| Pagamento (vários métodos, troco, valor da nota) | F4 | OK |
| Pagamento parcial, comanda **reaberta** com saldo pendente | F4, F1.14 | OK |
| Fechar, reabrir, cancelar comanda | F1.13, F1.14, F1.5 | OK |
| Pré-conta e comprovante imprimível | F1.15, F7 | OK |
| Histórico/linha do tempo de eventos da comanda | F1 (histórico), F10 linha do tempo | OK |
| Controle de versão contra edição simultânea | F1 (versão da comanda), S10 | ADICIONADO |
| Promoção aplicada automaticamente ao item, com registro | F2.4, F1 | AMPLIADO |
| Cronômetro de tempo aberto da comanda | F1 | ADICIONADO |
| Atalho do garçom no modal de nova comanda | F1 | ADICIONADO |
| Comissão calculada no fechamento | F9 | OK |
| Lista de comandas fechadas | F1 lista, F10 histórico | OK |

## 4. Cardápio

| Sistema atual | Novo | Status |
|---|---|---|
| Produto (nome, categoria, preço, ativo/inativo) | F2.1 | OK |
| Preço promocional que se sobrepõe | F2 | ADICIONADO (F2.11) |
| Categorias com 2 níveis; filtro por subcategoria | F2.10 | ADICIONADO |
| Ficha técnica (insumos e quantidades) | F2.6 | OK |
| Produto sem ficha: vende, sem CMV | F2 (ficha opcional) | OK |
| **Produção possível** por produto (quantas unidades o estoque permite) | F2.9 | ADICIONADO |
| Top produtos | F10 | OK |
| Promoções: % ou valor, janelas de data e hora, recorrência semanal/mensal, produtos vinculados, visão mensal | F2.4, F2.12 | AMPLIADO |
| Combos | F2.5 | OK (novo) |
| Sub-receitas (previstas na V2 antiga) | F2.6 | OK |

## 5. Estoque, insumos, compras e fornecedores

| Sistema atual | Novo | Status |
|---|---|---|
| Cadastro de insumos: unidade base, qtd por caixa, custo médio, estoque, reservado, nível crítico, EAN | F3.1, F3.12, F3.13 | AMPLIADO/ADICIONADO |
| Saldo, críticos, movimentações por insumo e por produto | F3.9, F3.8 | OK |
| Baixa sem venda com motivo (perda, quebra, cortesia, outro) | F3.7 | OK |
| Tipos de movimento: entrada, saída venda/perda/consumo interno, estorno de entrada, **estorno de compra** | F3 tipos | AMPLIADO (estorno de compra) |
| Reserva de estoque no lançamento, sem condição de corrida | F3.12, S10 | ADICIONADO |
| Alerta de insumo crítico (banner/toast/badge) | F3.8, F13 | OK |
| Fornecedores (nome, telefone, e-mail, CNPJ, ativo) | F3.2 | OK |
| Compras imediata, agendada, a prazo, com datas e hora previstas | F3.15 | ADICIONADO |
| Confirmar recebimento, cancelar, editar compra | F3.3, F3.15 | OK |
| **Importação de NF-e (XML) com match automático**, validação segura do arquivo | F3.16 | AMPLIADO |
| Compra gera conta a pagar | F8.2 | OK |
| **Consumo interno** (por consumidor, em lote, resumo mensal, estorno) | F3.11 (**simplificado**: baixa de estoque com motivo `consumo_interno`, sem tela própria; decisão do produto) | ADICIONADO (reduzido) |

## 6. Financeiro

| Sistema atual | Novo | Status |
|---|---|---|
| Contas a pagar (lista, resumo, pagar com método) | F8.1 | OK |
| Notificações de vencimento, marcar como lida | F13 | OK |
| DRE | F8.8, F10 | OK |
| CMV por produto | F8.7, F10 | OK |
| Comissões de garçom (percentual, pago/pendente, ajustar, excluir) | F9 | AMPLIADO |
| Contas a receber, contas financeiras, fluxo de caixa | F8 | OK (novo) |

## 7. Caixa

| Sistema atual | Novo | Status |
|---|---|---|
| Abrir e fechar sessão de caixa, valor informado, esperado e diferença | F5.1, F5.4 | OK |
| Sangria e suprimento | F5.2, F5.3 | OK |
| Relatório de fechamento de caixa | F10 | OK |

## 8. Cadastros de apoio

| Sistema atual | Novo | Status |
|---|---|---|
| Garçons (CRUD, ativo, estatísticas) | F9 | AMPLIADO (estatísticas) |
| Métodos de pagamento (tipo, ativo, padrão) | F4.1 | OK |
| Dados do estabelecimento (nome, CNPJ, endereço, telefone) | F14 | OK |

## 9. Relatórios e dashboard

| Sistema atual | Novo | Status |
|---|---|---|
| Vendas do dia; histórico de comandas; fechamento de caixa; DRE; CMV por produto; perdas e cortesias; vendas por garçom; produtos mais vendidos; pico de vendas por horário; vendas por produto | F10 relatórios | AMPLIADO (lista nominal mantida) |
| Índice de relatórios | F10 | OK |
| Widgets do dashboard (faturamento hoje/ontem/mês, ticket médio, CMV, lucro, comandas abertas, por hora, mapa de calor, formas de pagamento, garçons, comissões, cortesias/perdas, estoque em atenção, compras agendadas, top produtos, histórico, resumo anual) | F10 dashboard | AMPLIADO (lista nominal mantida) |

## 10. Calendário

| Sistema atual | Novo | Status |
|---|---|---|
| Eventos do estabelecimento (CRUD) | F15.1, F15.2 (calendário completo com recorrência) | ADICIONADO (ampliado) |
| **Cockpit consolidado** (eventos, promoções, contas a pagar, entregas de insumo) | F15.4, F15.5 (camadas, notificações no calendário configuráveis) | ADICIONADO |

## 11. Comunicados e notificações

| Sistema atual | Novo | Status |
|---|---|---|
| Comunicados da plataforma, com expiração, alvo e leitura por usuário | F13.10, F12.16 | AMPLIADO |
| Notificações in-app (contas, estoque) | F13 | OK |

## 12. Plataforma (painel de administração)

| Sistema atual | Novo | Status |
|---|---|---|
| Login de administrador da plataforma, identidade separada | F12, D12 | OK |
| Organizações (criar, editar, listar, detalhe) | F12.2 | OK |
| Usuários e perfis de cada organização | F12.13 | ADICIONADO |
| Assinatura: estados, histórico, pagamento manual, bloqueio, trial | F12.11 (5 estados: em teste personalizável, ativa com tolerância, suspensa, bloqueada, encerrada) | ADICIONADO (ampliado) |
| Telas informativas de bloqueio (vencida, suspensa) | F12.11 | ADICIONADO |
| Planos (preço mensal) e limite de usuários | F12.12 | ADICIONADO |
| Feature flags por organização | F12.4 | OK |
| Impersonação, com atribuição ao admin real | F12.6, D15 | OK |
| Cockpit de métricas global e por organização | F12.14 | ADICIONADO |
| Configurações da plataforma (ex.: duração do teste) | F12.15 | ADICIONADO |
| Auditoria da plataforma | F12.7 | OK |
| Comunicados | F12.5, F12.16 | OK |

## 13. Transversais e operação

| Sistema atual | Novo | Status |
|---|---|---|
| Isolamento por organização no banco (RLS) | S5 | OK |
| Auditoria com antes/depois e "impersonado por" | F10, S14 | OK |
| Limites de taxa e proteção de rotas | S11 | OK |
| Responsividade para celular e tablet | RNF2, RNF8 | OK |
| Monitoramento de erros (Sentry) e health check | RNF6 | OK |
| Bloco "Empresa" e identidade da organização na interface | `05-ux-design` | PENDENTE (design) |
| Backup acessível pelo usuário (removido por risco de vazamento) | Fora de escopo; exportação de dados controlada em F14 e RNF5 | OK |

## 14. Itens planejados e ainda não construídos no sistema atual (para decidir)

| Item | Origem | Destino proposto |
|---|---|---|
| Permissões granulares ALLOW/DENY | `v2-melhorias` (backlog) | Já é requisito base (D4, D10, S8) |
| Sub-receitas | `v1-pendencias` | F2.6 |
| Módulo de planos e precificação com cobrança | `v2-melhorias` | F12.3, F12.11, F12.12 |
| Atributos obrigatórios de ator em todos os eventos de comanda | `v1-pendencias` | F1, S14 (ator sempre registrado) |

## Resultado
- **Nenhuma funcionalidade do sistema atual ficou sem requisito.**
- **Faltavam e foram adicionadas:** calendário e agenda consolidada (F15), consumo interno como feature própria (F3.11), produção possível, categorias em dois níveis, tipos de compra, estorno de compra, EAN e embalagem, modelos de permissão, ciclo completo da assinatura (teste, histórico, bloqueio com telas informativas), planos e limites, cockpit e configurações da plataforma, gestão de usuários por organização, e vários detalhes de comanda (numeração diária, nome ou mesa, pessoas, cronômetro, versão).
- **Pendente apenas de design:** bloco "Empresa" e telas de bloqueio, tratados em `05-ux-design`.

## Como esta lista deve ser usada
Ao fechar o `06-roadmap`, cada linha acima vira rastreável a uma entrega. Nada daqui é obrigação de copiar o comportamento atual: é a garantia de que o **requisito** existe e pode ser aprovado, melhorado ou descartado conscientemente.
