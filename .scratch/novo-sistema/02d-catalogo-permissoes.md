# Novo sistema — Catálogo de permissões e perfis

Status: rascunho 1. Consolida as permissões de `02-features` (F1 a F15) e propõe perfis padrão. Permissões da Control Tower estão na seção final.

## 1. Como as permissões e os perfis funcionam

- **Permissão** é uma ação específica sobre um recurso (`recurso:ação`). Cada feature declara as suas; o catálogo é gerado do código, então uma feature nova aparece sozinha na tela de perfis.
- **Perfil é um grupo de permissões** com um nome. Um usuário recebe um perfil; permissões individuais (extras ou negadas) são exceção auditada.
- **Perfis padrão** (de fábrica) vêm prontos e servem de ponto de partida: não são editáveis, mas podem ser **duplicados**. **Perfis personalizados** são criados pela organização, editados livremente e podem ser **vinculados a um modelo** (acompanham mudanças do modelo) ou **copiados** (independentes).
- **Escopo:** cada concessão vale para a organização inteira ou para unidades específicas (ex.: gerente só da Unidade 2).
- **Limites e condições [L]:** valor, percentual, horário, tipo de dispositivo, por perfil (ex.: desconto até 5% para o garçom, até 20% para o gerente).
- **Regras de concessão:** só se concede o que se possui; ninguém altera as próprias permissões; deny explícito vence allow; sempre existe um dono.
- **Auditoria [A]:** toda ação marcada exige motivo e é registrada com ator, antes/depois e o limite aplicado.

## 2. Perfis padrão propostos

| Perfil padrão | Para quem | Resumo |
|---|---|---|
| **D — Dono** | Dono da organização | Tudo da organização. Não pode ser rebaixado nem desativado por outros |
| **G — Gerente** | Gerente de unidade | Operação completa da(s) unidade(s) do escopo; aprova exceções; sem configuração da organização, perfis, fiscal e política de segurança |
| **C — Caixa** | Operador de caixa | Comandas, pagamentos, caixa próprio, comprovantes; limites baixos |
| **W — Garçom** | Salão | Abrir e lançar nas próprias comandas, dividir, pedir reimpressão; sem desconto nem cortesia |
| **E — Estoquista** | Estoque e compras | Insumos, movimentações, inventário, compras e fornecedores |
| **F — Financeiro** | Financeiro/contador interno | Contas, conciliação, relatórios financeiros, DRE; sem operação de salão |
| **K — Cozinha/Bar** | Praças | Fila de preparo, marcar pronto, baixa de perdas e inventário da praça |
| **R — Somente leitura** | Contador externo, sócio | Consulta de relatórios e telas, sem nenhuma ação que altere dado |

Legenda da tabela: letra **maiúscula** = concedida; **minúscula** = concedida com **limite ou escopo restrito por padrão** (ex.: desconto até um percentual baixo, só o próprio turno).
Marcadores: **A** = ação auditada com motivo obrigatório; **L** = aceita limite/condição (valor, %, horário).


## 3. Permissões da organização por módulo

### Comandas (F1)
Operação de salão.

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `comanda:ver` | Ver comandas das unidades do escopo (as próprias, se faltar `ver_comandas_de_outros`) |  | ● | ● | ● | ● |  |  |  | ● |
| `comanda:ver_comandas_de_outros` | Ver comandas de outros garçons |  | ● | ● | ● |  |  |  |  | ● |
| `comanda:criar` | Abrir comanda |  | ● | ● | ● | ● |  |  |  |  |
| `comanda:lancar_item` | Lançar item |  | ● | ● | ● | ● |  |  |  |  |
| `comanda:alterar_item` | Alterar quantidade/observação |  | ● | ● | ● | ● |  |  |  |  |
| `comanda:cancelar_item` | Cancelar item, com motivo | A | ● | ● | ● | ◐ |  |  |  |  |
| `comanda:cancelar` | Cancelar a comanda inteira, com motivo | A | ● | ● |  |  |  |  |  |  |
| `comanda:cortesia` | Marcar item como cortesia, com motivo | A L | ● | ● |  |  |  |  |  |  |
| `comanda:brinde` | Marcar item como brinde (ação comercial) | A L | ● | ● |  |  |  |  |  |  |
| `comanda:desconto` | Aplicar desconto em item ou comanda; limite % por perfil | A L | ● | ● | ◐ |  |  |  |  |  |
| `comanda:remover_taxa_servico` | Remover taxa de serviço | A | ● | ● |  |  |  |  |  |  |
| `comanda:transferir_item` | Mover item entre comandas |  | ● | ● | ● | ● |  |  |  |  |
| `comanda:transferir_mesa` | Mudar a comanda de mesa |  | ● | ● | ● | ● |  |  |  |  |
| `comanda:juntar` | Juntar ou separar comandas |  | ● | ● | ● |  |  |  |  |  |
| `comanda:dividir` | Dividir a conta |  | ● | ● | ● | ● |  |  |  |  |
| `comanda:receber_pagamento` | Receber pagamento na comanda |  | ● | ● | ● |  |  |  |  |  |
| `comanda:estornar_pagamento` | Estornar pagamento da comanda | A | ● | ● |  |  |  |  |  |  |
| `comanda:fechar` | Fechar comanda |  | ● | ● | ● |  |  |  |  |  |
| `comanda:reabrir` | Reabrir comanda fechada | A | ● | ● |  |  |  |  |  |  |
| `comanda:fechar_forcado` | Fechar comanda esquecida à força | A | ● | ● |  |  |  |  |  |  |
| `comanda:renomear` | Renomear/identificar comanda |  | ● | ● | ● | ● |  |  |  |  |
| `comanda:trocar_garcom` | Trocar o garçom responsável |  | ● | ● |  |  |  |  |  |  |
| `comanda:editar_observacao` | Observação geral da comanda |  | ● | ● | ● | ● |  |  |  |  |
| `comanda:ver_historico` | Ver linha do tempo da comanda |  | ● | ● | ● |  |  |  |  | ● |
| `comanda:reimprimir` | Reimprimir pré-conta/comprovante |  | ● | ● | ● | ● |  |  |  |  |

### Cardápio (F2)
Produtos, preços, promoções, combos e fichas.

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `cardapio:ver` | Ver cardápio e disponibilidade |  | ● | ● | ● | ● | ● | ● | ● | ● |
| `cardapio:produto_criar` | Criar produto |  | ● | ● |  |  |  |  |  |  |
| `cardapio:produto_editar` | Editar produto |  | ● | ● |  |  |  |  |  |  |
| `cardapio:produto_desativar` | Desativar/reativar produto |  | ● | ● |  |  |  |  |  |  |
| `cardapio:preco_alterar` | Alterar preço (limite de variação %) | A L | ● | ● |  |  |  |  |  |  |
| `cardapio:promocao_gerenciar` | Criar e editar promoções |  | ● | ● |  |  |  |  |  |  |
| `cardapio:combo_gerenciar` | Criar e editar combos |  | ● | ● |  |  |  |  |  |  |
| `cardapio:ficha_ver` | Ver ficha técnica |  | ● | ● |  |  | ● |  |  | ● |
| `cardapio:ficha_editar` | Editar ficha técnica | A | ● | ● |  |  |  |  |  |  |
| `cardapio:custo_ver` | Ver custo e margem (dado sensível) |  | ● | ● |  |  |  | ● |  | ● |
| `cardapio:marcar_indisponivel` | Marcar item como acabou |  | ● | ● | ● | ● | ● |  | ● |  |

### Estoque e compras (F3)
Insumos, movimentações, inventário, fornecedores, compras.

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `estoque:ver` | Ver saldos e extrato |  | ● | ● |  |  | ● | ● | ● | ● |
| `estoque:ver_custo` | Ver custo médio e valor em estoque |  | ● | ● |  |  | ● | ● |  | ● |
| `insumo:criar` | Cadastrar insumo |  | ● | ● |  |  | ● |  |  |  |
| `insumo:editar` | Editar insumo |  | ● | ● |  |  | ● |  |  |  |
| `insumo:desativar` | Desativar insumo |  | ● | ● |  |  | ● |  |  |  |
| `estoque:entrada_manual` | Entrada manual de estoque | A | ● | ● |  |  | ● |  |  |  |
| `estoque:ajustar` | Ajuste de estoque (limite de valor) | A L | ● | ● |  |  | ◐ |  |  |  |
| `estoque:registrar_perda` | Registrar perda/quebra/cortesia | A | ● | ● |  |  | ● |  | ● |  |
| `estoque:consumo_interno` | Registrar consumo interno (limite diário opcional) | A L | ● | ● |  |  | ● |  | ◐ |  |
| `estoque:consumo_interno_estornar` | Estornar consumo interno | A | ● | ● |  |  |  |  |  |  |
| `estoque:inventario_contar` | Contar inventário |  | ● | ● |  |  | ● |  | ● |  |
| `estoque:inventario_aprovar` | Aprovar divergências de inventário | A | ● | ● |  |  |  |  |  |  |
| `estoque:transferir` | Transferir entre unidades/locais |  | ● | ● |  |  | ● |  |  |  |
| `estoque:alerta_configurar` | Configurar limites de alerta por insumo |  | ● | ● |  |  | ● |  |  |  |
| `estoque:alerta_receber` | Receber alertas de estoque |  | ● | ● |  |  | ● |  | ● |  |
| `estoque:politica_negativo_configurar` | Permitir/bloquear estoque negativo | A | ● |  |  |  |  |  |  |  |
| `fornecedor:ver` | Ver fornecedores |  | ● | ● |  |  | ● | ● |  | ● |
| `fornecedor:gerenciar` | Criar/editar fornecedores |  | ● | ● |  |  | ● |  |  |  |
| `compra:criar` | Criar compra |  | ● | ● |  |  | ● |  |  |  |
| `compra:receber` | Receber compra |  | ● | ● |  |  | ● |  |  |  |
| `compra:cancelar` | Cancelar compra | A | ● | ● |  |  |  |  |  |  |
| `compra:importar_nfe` | Importar NF-e XML |  | ● | ● |  |  | ● | ● |  |  |
| `compra:ver_precos` | Ver preços de compra |  | ● | ● |  |  | ● | ● |  | ● |

### Pagamentos (F4)
Recebimento, métodos, fiado, vouchers.

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `pagamento:ver` | Ver pagamentos |  | ● | ● | ● |  |  | ● |  | ● |
| `pagamento:registrar` | Registrar pagamento |  | ● | ● | ● |  |  |  |  |  |
| `pagamento:dividir` | Dividir pagamento |  | ● | ● | ● | ● |  |  |  |  |
| `pagamento:registrar_gorjeta` | Registrar gorjeta |  | ● | ● | ● |  |  |  |  |  |
| `pagamento:estornar` | Estornar pagamento (limite de valor) | A L | ● | ● | ◐ |  |  |  |  |  |
| `pagamento:aceitar_fiado` | Aceitar fiado/conta de cliente (limite) | A L | ● | ● | ◐ |  |  |  |  |  |
| `metodo_pagamento:gerenciar` | Criar/editar métodos de pagamento | A | ● | ● |  |  |  |  |  |  |
| `metodo_pagamento:ver_taxas` | Ver taxas das operadoras |  | ● | ● |  |  |  | ● |  | ● |
| `voucher:emitir` | Emitir voucher (limite) | A L | ● | ● |  |  |  |  |  |  |
| `voucher:usar` | Usar voucher |  | ● | ● | ● | ● |  |  |  |  |
| `conciliacao:ver` | Ver conciliação |  | ● | ● |  |  |  | ● |  | ● |
| `conciliacao:ajustar` | Ajustar conciliação | A | ● | ● |  |  |  | ● |  |  |

### Caixa (F5)
Turnos, sangria, suprimento, fechamento.

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `caixa:ver` | Ver caixa (o próprio turno, sem `ver_todos`) |  | ● | ● | ● |  |  | ● |  | ● |
| `caixa:abrir` | Abrir caixa |  | ● | ● | ● |  |  |  |  |  |
| `caixa:suprimento` | Suprimento (limite) | A L | ● | ● | ◐ |  |  |  |  |  |
| `caixa:sangria` | Sangria (limite) | A L | ● | ● | ◐ |  |  |  |  |  |
| `caixa:fechar` | Fechar o próprio caixa |  | ● | ● | ● |  |  |  |  |  |
| `caixa:fechar_cego` | Fechamento com conferência cega |  |  |  | ● |  |  |  |  |  |
| `caixa:aprovar_diferenca` | Aprovar diferença de caixa (limite) | A L | ● | ● |  |  |  |  |  |  |
| `caixa:reabrir` | Reabrir caixa | A | ● | ● |  |  |  |  |  |  |
| `caixa:ver_todos` | Ver todos os turnos |  | ● | ● |  |  |  | ● |  | ● |
| `caixa:configurar_janela` | Configurar janela operacional | A | ● | ● |  |  |  |  |  |  |
| `caixa:registrar_fora_do_horario` | Registrar fora da janela permitida | A | ● |  |  |  |  |  |  |  |

### Cozinha e bar (F6)
Praças, fila de preparo, impressão.

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `cozinha:ver_fila` | Ver fila de preparo da praça |  | ● | ● |  | ● |  |  | ● |  |
| `cozinha:marcar_preparo` | Marcar em preparo |  | ● | ● |  |  |  |  | ● |  |
| `cozinha:marcar_pronto` | Marcar pronto |  | ● | ● |  |  |  |  | ● |  |
| `cozinha:reenviar_pedido` | Reenviar pedido à praça |  | ● | ● | ● | ● |  |  | ● |  |
| `cozinha:configurar_pracas` | Configurar praças e destinos | A | ● | ● |  |  |  |  |  |  |
| `impressora:gerenciar` | Cadastrar/gerenciar impressoras | A | ● | ● |  |  |  |  |  |  |

### Comprovantes e fiscal (F7)

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `comprovante:reimprimir` | Reimprimir comprovante |  | ● | ● | ● | ● |  |  |  |  |
| `fiscal:ver` | Ver documentos fiscais |  | ● | ● |  |  |  | ● |  | ● |
| `fiscal:emitir` | Emitir nota fiscal |  | ● | ● | ● |  |  |  |  |  |
| `fiscal:cancelar_nota` | Cancelar nota fiscal | A | ● | ● |  |  |  |  |  |  |
| `fiscal:configurar` | Configurar dados fiscais e certificado | A | ● |  |  |  |  |  |  |  |

### Financeiro (F8)
Contas, fluxo, DRE.

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `financeiro:ver` | Ver o módulo financeiro |  | ● | ● |  |  |  | ● |  | ● |
| `financeiro:ver_dre` | Ver DRE e resultado |  | ● |  |  |  |  | ● |  | ● |
| `conta_pagar:criar` | Criar conta a pagar |  | ● | ● |  |  |  | ● |  |  |
| `conta_pagar:editar` | Editar conta a pagar |  | ● | ● |  |  |  | ● |  |  |
| `conta_pagar:pagar` | Pagar conta (limite de valor) | A L | ● | ● |  |  |  | ◐ |  |  |
| `conta_pagar:cancelar` | Cancelar conta | A | ● |  |  |  |  | ● |  |  |
| `conta_receber:ver` | Ver contas a receber |  | ● | ● |  |  |  | ● |  | ● |
| `conta_receber:baixar` | Dar baixa em recebimento | A | ● | ● |  |  |  | ● |  |  |
| `conta_financeira:gerenciar` | Gerenciar contas financeiras | A | ● |  |  |  |  | ● |  |  |
| `conta_financeira:transferir` | Transferir entre contas (limite) | A L | ● |  |  |  |  | ● |  |  |
| `financeiro:categorias_gerenciar` | Plano de contas e centros de custo |  | ● |  |  |  |  | ● |  |  |
| `financeiro:exportar` | Exportar dados financeiros | A | ● |  |  |  |  | ● |  |  |

### Equipe e comissões (F9)

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `equipe:ver` | Ver colaboradores |  | ● | ● |  |  |  | ● |  | ● |
| `equipe:colaborador_gerenciar` | Criar/editar colaboradores |  | ● | ● |  |  |  |  |  |  |
| `comissao:ver_proprias` | Ver a própria comissão |  | ● | ● | ● | ● | ● | ● | ● |  |
| `comissao:ver_todas` | Ver comissões de todos |  | ● | ● |  |  |  | ● |  | ● |
| `comissao:configurar` | Configurar regra de comissão | A | ● | ● |  |  |  |  |  |  |
| `comissao:pagar` | Pagar comissão | A | ● | ● |  |  |  | ● |  |  |
| `comissao:ajustar` | Ajustar valor de comissão (limite) | A L | ● | ● |  |  |  |  |  |  |

### Relatórios, dashboard e auditoria (F10)

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `relatorio:ver_vendas` | Relatórios de vendas |  | ● | ● |  |  |  | ● |  | ● |
| `relatorio:ver_estoque` | Relatórios de estoque |  | ● | ● |  |  | ● |  |  | ● |
| `relatorio:ver_financeiro` | Relatórios financeiros |  | ● |  |  |  |  | ● |  | ● |
| `relatorio:ver_custos` | Relatórios de custo/CMV |  | ● | ● |  |  |  | ● |  | ● |
| `relatorio:exportar` | Exportar relatórios | A | ● | ● |  |  |  | ● |  |  |
| `relatorio:ver_todas_unidades` | Ver todas as unidades |  | ● |  |  |  |  | ● |  | ● |
| `dashboard:ver` | Ver dashboard |  | ● | ● | ● |  |  | ● |  | ● |
| `dashboard:configurar` | Configurar widgets |  | ● | ● |  |  |  |  |  |  |
| `auditoria:ver` | Ver log de auditoria |  | ● | ● |  |  |  |  |  |  |
| `auditoria:exportar` | Exportar auditoria | A | ● |  |  |  |  |  |  |  |

### Identidade e acesso (F11)
Usuários, perfis, dispositivos, sessões.

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `usuario:ver` | Ver usuários |  | ● | ● |  |  |  |  |  |  |
| `usuario:convidar` | Convidar/criar usuário | A | ● | ● |  |  |  |  |  |  |
| `usuario:desativar` | Desativar/reativar usuário | A | ● | ● |  |  |  |  |  |  |
| `usuario:redefinir_acesso` | Redefinir acesso | A | ● | ● |  |  |  |  |  |  |
| `perfil:ver` | Ver perfis |  | ● | ● |  |  |  |  |  |  |
| `perfil:gerenciar` | Criar/editar perfis | A | ● |  |  |  |  |  |  |  |
| `permissao:atribuir` | Atribuir perfil/permissões a usuários (só o que a pessoa já tem) | A | ● |  |  |  |  |  |  |  |
| `dispositivo:registrar` | Registrar dispositivo do salão | A | ● | ● |  |  |  |  |  |  |
| `dispositivo:revogar` | Revogar dispositivo | A | ● | ● |  |  |  |  |  |  |
| `sessao:ver_todas` | Ver sessões ativas |  | ● | ● |  |  |  |  |  |  |
| `sessao:revogar` | Encerrar sessões | A | ● | ● |  |  |  |  |  |  |
| `seguranca:politica_editar` | Política de senha, sessão e PIN | A | ● |  |  |  |  |  |  |  |
| `seguranca:exigir_2fa` | Exigir 2FA (reservado) | A | ● |  |  |  |  |  |  |  |

### Notificações (F13)

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `notificacao:ver_proprias` | Ver as próprias notificações |  | ● | ● | ● | ● | ● | ● | ● | ● |
| `notificacao:preferencias_editar` | Editar preferências pessoais |  | ● | ● | ● | ● | ● | ● | ● |  |
| `notificacao:regras_configurar` | Configurar regras | A | ● | ● |  |  |  |  |  |  |
| `notificacao:modelos_editar` | Editar modelos de mensagem | A | ● |  |  |  |  |  |  |  |
| `notificacao:destinatarios_definir` | Definir destinatários | A | ● | ● |  |  |  |  |  |  |
| `notificacao:ver_entregas` | Ver histórico de entregas |  | ● | ● |  |  |  |  |  |  |
| `notificacao:canal_pago_configurar` | Canais pagos (limite) | A L | ● |  |  |  |  |  |  |  |

### Configurações (F14)

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `configuracao:ver` | Ver configurações |  | ● | ● |  |  |  |  |  |  |
| `configuracao:organizacao_editar` | Editar configurações da organização | A | ● |  |  |  |  |  |  |  |
| `configuracao:unidade_editar` | Editar configurações da unidade | A | ● | ● |  |  |  |  |  |  |
| `configuracao:travar_opcao` | Travar/liberar opção para unidades | A | ● |  |  |  |  |  |  |  |
| `configuracao:ver_historico` | Ver histórico de configurações |  | ● | ● |  |  |  |  |  |  |
| `configuracao:restaurar` | Restaurar versão de configuração | A | ● |  |  |  |  |  |  |  |
| `organizacao:configurar` | Dados cadastrais da organização | A | ● |  |  |  |  |  |  |  |
| `unidade:criar` | Criar unidade (limite do plano) | A L | ● |  |  |  |  |  |  |  |
| `unidade:gerenciar` | Gerenciar unidades | A | ● | ● |  |  |  |  |  |  |
| `unidade:desativar` | Desativar unidade | A | ● |  |  |  |  |  |  |  |

### Calendário (F15)

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `calendario:ver` | Ver calendário |  | ● | ● | ● | ● | ● | ● | ● | ● |
| `calendario:ver_de_outros` | Ver eventos de outros |  | ● | ● |  |  |  |  |  | ● |
| `calendario:evento_criar` | Criar evento |  | ● | ● |  |  |  |  |  |  |
| `calendario:evento_editar` | Editar evento |  | ● | ● |  |  |  |  |  |  |
| `calendario:evento_excluir` | Excluir evento | A | ● | ● |  |  |  |  |  |  |
| `calendario:evento_recorrente_editar_serie` | Editar série recorrente |  | ● | ● |  |  |  |  |  |  |
| `calendario:ver_pagamentos` | Ver camada de pagamentos (exige `financeiro:ver`) |  | ● | ● |  |  |  | ● |  | ● |
| `calendario:ver_entregas` | Ver camada de entregas |  | ● | ● |  |  | ● |  |  | ● |
| `calendario:ver_promocoes` | Ver camada de promoções |  | ● | ● | ● | ● |  |  |  | ● |
| `calendario:configurar_camadas` | Configurar camadas | A | ● | ● |  |  |  |  |  |  |
| `calendario:configurar_notificacoes` | Configurar notificações no calendário | A | ● | ● |  |  |  |  |  |  |
| `calendario:compartilhar` | Compartilhar calendário |  | ● | ● |  |  |  |  |  |  |
| `calendario:exportar_ical` | Exportar/assinar iCal | A | ● |  |  |  |  |  |  |  |

### Mesas, clientes e importação (F1, F16, F17)

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `mesa:ver` | Ver mapa de mesas | | ● | ● | ● | ● |  |  | ● | ● |
| `mesa:gerenciar` | Criar, mover e desativar mesas e áreas | A | ● | ● |  |  |  |  |  |  |
| `cliente:ver` | Ver clientes | | ● | ● | ● | ◐ |  | ● |  | ● |
| `cliente:gerenciar` | Criar e editar clientes | | ● | ● | ● | ◐ |  |  |  |  |
| `cliente:ver_dados_pessoais` | Ver documento e nascimento (mascarados sem isso) | A | ● | ● |  |  |  | ● |  |  |
| `cliente:ver_historico` | Ver histórico de consumo | | ● | ● | ● |  |  | ● |  | ● |
| `cliente:mesclar` | Mesclar duplicados | A | ● | ● |  |  |  |  |  |  |
| `cliente:exportar` | Exportar dados de clientes | A | ● |  |  |  |  |  |  |  |
| `cliente:anonimizar` | Anonimizar cliente (LGPD) | A | ● |  |  |  |  |  |  |  |
| `importacao:executar` | Importar planilhas | A | ● | ● |  |  | ● |  |  |  |
| `importacao:ver_historico` | Ver histórico de importações | | ● | ● |  |  | ● |  |  |  |
| `importacao:desfazer` | Desfazer importação | A | ● | ● |  |  |  |  |  |  |
| `importacao:modelos_baixar` | Baixar modelos de planilha | | ● | ● |  |  | ● | ● |  |  |
| `exportacao:executar` | Exportar listas | A | ● | ● |  |  | ● | ● |  |  |

### Gestão avançada (F18)

| Permissão | O que permite | Marc. | D | G | C | W | E | F | K | R |
|---|---|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `resumo_dia:receber` | Receber o resumo do dia | | ● | ● |  |  |  | ● |  | ● |
| `resumo_dia:configurar` | Configurar conteúdo e destinatários | A | ● | ● |  |  |  |  |  |  |
| `painel:ver_ao_vivo` | Painel ao vivo da unidade | | ● | ● |  |  |  | ● |  | ● |
| `painel:ver_consolidado` | Painel ao vivo consolidado | | ● |  |  |  |  | ● |  | ● |
| `margem:ver` | Ver margem e alertas de margem | | ● | ● |  |  |  | ● |  | ● |
| `margem:configurar_alvo` | Definir margem-alvo | A | ● | ● |  |  |  |  |  |  |
| `margem:simular` | Usar o simulador de preço | | ● | ● |  |  |  | ● |  |  |
| `preco:aplicar_sugestao` | Aplicar preço sugerido (limite de variação) | A L | ● | ◐ |  |  |  |  |  |  |
| `bebida:ver_rendimento` | Ver rendimento de barril, doses e desvios | | ● | ● |  |  | ● |  | ● | ● |
| `barril:trocar` | Registrar troca de barril | A | ● | ● |  |  | ● |  | ● |  |
| `barril:encerrar` | Encerrar barril e registrar perda | A | ● | ● |  |  | ● |  | ● |  |
| `garrafa:abrir` | Registrar abertura de garrafa | | ● | ● |  |  | ● |  | ● |  |
| `bebida:configurar_perdas` | Perda de espuma e rendimento | A | ● | ● |  |  |  |  |  |  |
| `excecao:ver_painel` | Ver painel de exceções | | ● | ● |  |  |  |  |  | ● |
| `excecao:marcar_revisado` | Marcar exceção como revisada | A | ● | ● |  |  |  |  |  |  |
| `excecao:regras_configurar` | Regras de alerta de ações sensíveis | A | ● | ◐ |  |  |  |  |  |  |
| `documento:ver` | Ver documentos e vencimentos | | ● | ● |  |  |  |  |  | ● |
| `documento:gerenciar` | Criar/editar documentos | A | ● | ● |  |  |  |  |  |  |
| `documento:baixar_arquivo` | Baixar o arquivo anexo | | ● | ● |  |  |  |  |  | ● |
| `equipamento:ver` | Ver equipamentos e manutenções | | ● | ● |  |  | ● |  | ● | ● |
| `equipamento:gerenciar` | Criar/editar equipamentos e planos | A | ● | ● |  |  |  |  |  |  |
| `evento:ver_resultado` | Ver rentabilidade do evento | | ● | ● |  |  |  | ● |  | ● |
| `evento:editar_financeiro` | Editar cachê, custos e couvert | A | ● | ● |  |  |  |  |  |  |
| `compra:ver_historico_preco` | Ver histórico e ranking de preços | | ● | ● |  |  | ● | ● |  | ● |
| `orcamento:ver` | Ver orçamento por categoria | | ● | ● |  |  |  | ● |  | ● |
| `orcamento:gerenciar` | Definir orçamento | A | ● |  |  |  |  | ● |  |  |
| `pedido_compra:criar` | Criar pedido de compra | | ● | ● |  |  | ● |  |  |  |
| `pedido_compra:enviar` | Enviar pedido ao fornecedor | A | ● | ● |  |  | ● |  |  |  |

Legenda: ● concedida · ◐ concedida com limite ou escopo restrito · vazio = negada.

Total de permissões da organização neste rascunho: **205** (165 dos módulos F1 a F15, 13 de mesas, clientes e importação, e 27 de gestão avançada).

## 4. Control Tower: permissões e perfis da plataforma

Domínio de identidade separado (D12, D14). Perfis padrão: **Super admin** (tudo, sem restrição, protegido), **Suporte** (ver organizações, impersonar com política), **Financeiro da plataforma** (assinaturas, planos, cobrança), **Operações** (feature flags, comunicados, saúde da plataforma), **Somente leitura**. Administradores têm política de impersonação configurável por pessoa (D15).

| Permissão | O que permite | Marc. | Super | Suporte | Financeiro | Operações | Leitura |
|---|---|---|:-:|:-:|:-:|:-:|:-:|
| `plataforma:ver` | Ver painel e cockpit da plataforma |  | ● | ● | ● | ● | ● |
| `organizacao:ver` | Ver organizações, unidades e usuários |  | ● | ● | ● | ● | ● |
| `organizacao:criar` | Criar organização | A | ● |  |  | ● |  |
| `organizacao:editar` | Editar dados da organização | A | ● | ● |  | ● |  |
| `organizacao:excluir` | Excluir/anonimizar organização | A | ● |  |  |  |  |
| `usuario_organizacao:gerenciar` | Criar/editar usuários e perfis de uma organização, redefinir acesso | A | ● | ● |  |  |  |
| `plano:gerenciar` | Criar e editar planos e limites | A | ● |  | ● |  |  |
| `cobranca:ver` | Ver cobrança e pagamentos |  | ● |  | ● |  | ● |
| `cobranca:ajustar` | Ajustar cobrança (limite) | A L | ● |  | ● |  |  |
| `assinatura:ver` | Ver assinatura e histórico |  | ● | ● | ● | ● | ● |
| `assinatura:iniciar_teste` | Iniciar teste com N dias | A | ● |  | ● | ● |  |
| `assinatura:prorrogar_teste` | Prorrogar teste (limite de dias) | A L | ● |  | ● | ● |  |
| `assinatura:converter` | Converter teste em ativa | A | ● |  | ● |  |  |
| `assinatura:conceder_cortesia` | Conceder período gratuito (limite) | A L | ● |  | ● |  |  |
| `assinatura:registrar_pagamento` | Registrar pagamento | A | ● |  | ● |  |  |
| `assinatura:agendar_mudanca` | Agendar mudança de estado | A | ● |  | ● |  |  |
| `assinatura:excecao_manual` | Exceção manual até uma data (limite) | A L | ● |  | ● |  |  |
| `assinatura:suspender` | Suspender | A | ● |  | ● | ● |  |
| `assinatura:bloquear` | Bloquear (segurança/abuso/legal) | A | ● |  |  | ● |  |
| `assinatura:reativar` | Reativar | A | ● |  | ● | ● |  |
| `assinatura:cancelar` | Cancelar (agenda o encerramento) | A | ● |  | ● |  |  |
| `assinatura:configurar_politicas` | Padrões de teste, carência e fim de teste | A | ● |  |  |  |  |
| `unidade_organizacao:definir_limite` | Definir quantas unidades a organização pode ter | A | ● |  | ● |  |  |
| `unidade_organizacao:criar` | Criar unidades em nome da organização | A | ● | ● |  | ● |  |
| `feature_flag:gerenciar` | Liberar/retirar funcionalidades | A | ● |  |  | ● |  |
| `comunicado:publicar` | Publicar comunicados | A | ● |  |  | ● |  |
| `configuracao_plataforma:editar` | Parâmetros globais da plataforma | A | ● |  |  |  |  |
| `suporte:impersonar` | Entrar como usuário de uma organização | A | ● | ● |  |  |  |
| `suporte:impersonar_escrita` | Impersonar com escrita | A | ● |  |  |  |  |
| `suporte:impersonar_politica_configurar` | Definir política de impersonação de cada administrador | A | ● |  |  |  |  |
| `admin_plataforma:gerenciar` | Criar administradores e definir permissões | A | ● |  |  |  |  |
| `auditoria_plataforma:ver` | Ver auditoria da plataforma |  | ● | ● | ● | ● | ● |
| `operacao:ver_filas_e_jobs` | Ver filas, jobs e falhas de integração |  | ● |  |  | ● |  |
| `seguranca:emergencia` | Botões de emergência (revogar sessões, suspender, rotacionar segredos) | A | ● |  |  |  |  |
| `exportacao_dados:cliente` | Exportar dados de uma organização | A | ● |  |  |  |  |

Legenda dos perfis: S super admin · U suporte · F financeiro da plataforma · O operações · L somente leitura.

Total de permissões da plataforma neste rascunho: **34**.

## 5. Pontos a decidir

1. ~~Perfis padrão~~ **Decidido:** servem.
2. ~~Gerente e configuração da unidade~~ **Decidido:** é uma permissão. O dono pode configurar tudo, inclusive a unidade; concede ou retira do gerente conforme quiser. O padrão do gerente é editar a própria unidade.
3. **Limites padrão** de desconto, cortesia, sangria, estorno e consumo interno para cada perfil (sugestões entram no `05` ou na primeira entrega).
4. **Garçom vê custo ou preço de compra?** Hoje não.
5. **Quem aprova** divergências de caixa e de inventário: pela permissão `caixa:aprovar_diferenca` / `estoque:inventario_aprovar`, com limite por perfil; quem fechou não aprova a própria (autoaprovação configurável para negócios pequenos). Ver `02-features` F5.
