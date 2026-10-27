# 02f. Catálogo de notificações

Complementa a F13 (`02-features.md`). A F13 define **como** a notificação funciona (evento, regra, destinatário, canal, entrega). Este documento define **quais** avisos existem e como cada um aparece.

## 1. Os três tipos

| Tipo | O que é | Onde aparece | Some quando |
|---|---|---|---|
| **Ação** | Pede algo da pessoa ("confirmar recebimento", "aprovar diferença") | Aba **Ações** da bolinha | Alguém resolve, ou a condição deixa de existir (estoque voltou ao mínimo) |
| **Aviso** | Informa algo que aconteceu ("comanda fechada", "pedido recebido") | Aba **Avisos** da bolinha | A pessoa lê ou arquiva; expira pela retenção |
| **Estado** | Fato contínuo ("sem conexão", "caixa aberto há 14 h") | Faixa ou pílula fixa na tela; **não entra na central** | A condição acaba |

Regra de decisão: se a pessoa precisa **fazer algo** → Ação. Se só precisa **saber** → Aviso. Se vale **enquanto durar** → Estado.

Uma Ação que ninguém resolve não vira Aviso: continua Ação e, passado o prazo definido na regra, **escala** para quem pode resolver acima (ex.: caixa → gerente).

## 2. Como aparece (web)

- **Um sino azul na barra de cima** (ao lado da troca de tema), com um contador de **Ações pendentes** no canto. Sem Ações pendentes, mostra só um ponto se houver Avisos não lidos.
- Ao abrir: painel com duas abas, **Ações** (marcador laranja) e **Avisos** (marcador azul), e chips de área para filtrar (Estoque, Compras, Financeiro, Caixa, Salão, Segurança, Plataforma).
- **Pílula de chegada:** ao chegar algo novo, aparece uma pílula ao lado da bolinha por alguns segundos (`Ação · Pedido da Ambev chega hoje às 14h ↗`). Clicar abre a aba certa. Se ninguém clicar, some e o item fica guardado.
- **Gravidade** (`info`, `atenção`, `crítico`) é um marcador dentro do item, não a cor da aba. Crítico usa o rosa-carmim e vem primeiro na lista.
- Cor nunca é o único sinal: toda gravidade tem palavra e ícone.
- O que cada pessoa vê depende da **permissão** (F13, "destinatários resolvidos por permissão e escopo").

## 2b. Quem recebe: como a gestão define

1. **Padrão pela permissão:** cada aviso do catálogo (§3) tem um destinatário padrão, que é quem tem permissão de agir naquela área e unidade. Nada a configurar para começar.
2. **Ajuste da gestão** (`notificacao:destinatarios_definir`, auditado): matriz **tipo de aviso × perfil** em Configurações › Notificações, com uma caixa por célula. Células sem permissão ficam desabilitadas; obrigatórias ficam travadas. Chave geral por tipo.
3. **Exceção por pessoa**, na ficha do usuário: receber a mais ou deixar de receber (ex.: um gerente que não quer avisos de estoque).
4. **O funcionário não escolhe** o que recebe. Só controla o aparelho (som, vibração, push neste aparelho).
5. **Bar x cozinha:** perfis diferentes ("Bar", "Cozinha"), duplicados dos padrões. Avisos que dependem de pessoa (ex.: pedido pronto) vão ao **garçom responsável** da comanda, sem configuração.
6. **Vida da notificação:** lida some; ação resolvida some; o resto é apagado pela retenção. Sem arquivar, sem silenciar.

## 3. Catálogo

Legenda de canais: **App** (central, sempre), **Push** (tempo real no navegador/app), **E-mail** (resumo ou não urgente). WhatsApp/SMS ficam para depois (têm custo).
"Desliga?" diz se a organização pode desligar a regra. Segurança e cobrança não desligam (F13.7).

### Estoque

| Aviso | Tipo | Gravidade | Quem recebe | Canais | O clique leva a | Desliga? |
|---|---|---|---|---|---|---|
| Insumo abaixo do mínimo | Ação | atenção | Quem recebe alerta de estoque | App, Push | Insumo, com atalho "Criar pedido" | Sim |
| Insumo zerado ou negativo | Ação | crítico | Idem, mais gerente | App, Push | Insumo | Sim |
| Divergência no inventário a aprovar | Ação | atenção | Gerente | App | Contagem | Sim |
| Insumo perto da validade (se houver lote) | Ação | atenção | Quem recebe alerta de estoque | App | Lote | Sim |
| Insumo sem movimentação há N dias | Aviso | info | Quem recebe alerta de estoque | App, E-mail (resumo) | Insumo | Sim |
| Estoque reposto acima do mínimo | Aviso | info | Quem recebeu o alerta | App | Insumo | Sim |

### Compras e fornecedores

| Aviso | Tipo | Gravidade | Quem recebe | Canais | O clique leva a | Desliga? |
|---|---|---|---|---|---|---|
| Pedido agendado chega **hoje** (com horário) | Ação | info | Quem criou e quem recebe mercadoria | App, Push | Compra, com botão **Confirmar recebimento** | Sim |
| Pedido **atrasou** em relação ao horário previsto | Ação | atenção | Idem, mais gerente | App, Push | Compra (reagendar, cobrar, cancelar) | Sim |
| Pedido recebido com **divergência** | Ação | atenção | Gerente e financeiro | App | Compra, tela de conferência | Sim |
| **Hora de pedir** (rotina do fornecedor, ver §5) | Ação | info | Quem compra | App, Push | Nova compra pré-preenchida | Sim |
| Pedido recebido e conferido | Aviso | info | Quem criou | App | Compra | Sim |
| Preço de fornecedor subiu | Aviso | atenção | Quem compra, gerente | App, E-mail | Histórico de preços (I8) | Sim |
| Margem de produto caiu por aumento de insumo (F18.3) | Ação | atenção | Gerente, dono | App | Simulador de preço | Sim |

### Financeiro

| Aviso | Tipo | Gravidade | Quem recebe | Canais | O clique leva a | Desliga? |
|---|---|---|---|---|---|---|
| Conta a pagar vence em N dias | Ação | info | Financeiro | App, E-mail | Conta | Sim |
| Conta a pagar vence **hoje** | Ação | atenção | Financeiro, dono | App, Push | Conta, com atalho "Marcar paga" | Sim |
| Conta a pagar **atrasada** | Ação | crítico | Financeiro, dono | App, Push, E-mail | Conta | Sim |
| Fiado vencido | Ação | atenção | Financeiro, gerente | App | Cliente do fiado | Sim |
| Repasse de operadora atrasado | Ação | atenção | Financeiro | App | Contas a receber | Sim |
| Comissões a pagar no fechamento do período | Ação | info | Financeiro | App | Comissões | Sim |
| Orçamento da categoria estourou (I9) | Aviso | atenção | Dono, gerente | App, E-mail | Orçamento | Sim |
| Conta paga ou recebida | Aviso | info | Quem registrou | App | Conta | Sim |

### Caixa

| Aviso | Tipo | Gravidade | Quem recebe | Canais | O clique leva a | Desliga? |
|---|---|---|---|---|---|---|
| Diferença no fechamento a aprovar | Ação | atenção | Gerente | App, Push | Fechamento do caixa | Sim |
| Caixa aberto além da janela | Estado, vira Ação ao passar do limite | atenção | Operador do caixa, gerente | App | Caixa | Sim |
| Sangria ou suprimento registrado | Aviso | info | Gerente | App | Movimento | Sim |

### Salão e comandas

| Aviso | Tipo | Gravidade | Quem recebe | Canais | O clique leva a | Desliga? |
|---|---|---|---|---|---|---|
| Desconto ou cortesia acima da alçada aguardando aprovação | Ação | atenção | Gerente | App, Push | Comanda | Sim |
| Comanda aberta há muito tempo | Ação | atenção | Garçom responsável, gerente | App | Comanda | Sim |
| Item recusado ou indisponível na cozinha | Ação | atenção | Garçom responsável | App, Push | Comanda | Sim |
| Pedido pronto na cozinha | Aviso | info | Garçom responsável | App, Push | Comanda | Sim |
| Comanda fechada | Aviso | info | Gerente (se configurado) | App | Comanda | Sim |
| Cancelamento acima do limite | Aviso | atenção | Gerente, dono | App | Comanda e auditoria | Sim |

### Rotinas e documentos

| Aviso | Tipo | Gravidade | Quem recebe | Canais | O clique leva a | Desliga? |
|---|---|---|---|---|---|---|
| Checklist de abertura ou fechamento não feito (I11) | Ação | atenção | Responsável do turno, gerente | App, Push | Checklist | Sim |
| Documento perto do vencimento (alvará, bombeiros; I13) | Ação | atenção | Dono, gerente | App, E-mail | Documento | Sim |
| Evento do calendário próximo (F15) | Aviso | info | Participantes | App | Evento | Sim |
| Recado da gerência no mural (I15) | Aviso | info | Equipe do turno | App | Mural | Sim |
| Resumo do dia pronto (I4) | Aviso | info | Dono, gerente | App, E-mail | Relatório | Sim |

### Segurança (obrigatórias, F13.7)

| Aviso | Tipo | Gravidade | Quem recebe | Canais | O clique leva a | Desliga? |
|---|---|---|---|---|---|---|
| Login em aparelho ou local novo | Aviso | crítico | A própria pessoa e o dono | App, Push, E-mail | Sessões e dispositivos | **Não** |
| Senha ou permissão alterada | Aviso | crítico | A pessoa afetada e o dono | App, E-mail | Auditoria | **Não** |
| Falhas repetidas de login | Aviso | crítico | Dono | App, E-mail | Auditoria | **Não** |
| Exportação de dados em volume incomum | Aviso | crítico | Dono | App, E-mail | Auditoria | **Não** |
| Impersonação da plataforma na organização | Aviso | crítico | Dono | App, E-mail | Auditoria | **Não** |

### Plataforma

| Aviso | Tipo | Gravidade | Quem recebe | Canais | O clique leva a | Desliga? |
|---|---|---|---|---|---|---|
| Fatura em aberto ou vencendo | Ação | atenção | Dono | App, E-mail | Assinatura | **Não** |
| Limite do plano perto de estourar | Ação | atenção | Dono | App, E-mail | Plano | **Não** |
| Manutenção programada | Aviso | info | Todos | App | Comunicado | **Não** |
| Novidade do sistema | Aviso | info | Dono, gerente | App | Comunicado | Sim |

### Estados (não entram na central)

| Estado | Onde aparece |
|---|---|
| Sem conexão, com fila de ações pendentes de envio | Faixa fixa (web) e faixa fixa (celular) |
| Dados possivelmente desatualizados (falha ao atualizar) | Faixa de erro na tela |
| Modo protótipo | Marca no canto |

## 4. Regras de comportamento

1. **Dispara na transição**, não a cada ocorrência (F13.5): "abaixo do mínimo" avisa uma vez; só avisa de novo depois de voltar ao normal.
2. **Ação resolvida em outro lugar some sozinha** da lista de todos os destinatários (a conta paga na tela de contas encerra a Ação "conta vence hoje").
3. **Quem não tem permissão de resolver não recebe a Ação.** Recebe, no máximo, um Aviso.
4. **Agrupa** quando há muitos iguais ("3 insumos abaixo do mínimo"), com expansão dentro do item.
5. **Crítico** ignora janela de silêncio e vem primeiro.
6. **Sem dado sensível** em e-mail e mensagens (F13, requisitos). Dentro do app pode ter valor e nome.
7. **Ordem:** gravidade, depois mais recente.
8. **Pílula de chegada** só aparece para Ação e para Aviso crítico; Aviso comum entra quieto na aba.

## 5. Lacunas que este catálogo expõe nas features

- **Rotina do fornecedor** (nova, ainda não está em `02-features.md`): dia(s) fixo(s) de entrega e horário limite para pedir ("Ambev entrega terça e sexta; pedir até 16h na véspera"). Gera a Ação "Hora de pedir" e pré-preenche a compra agendada (F3.15). Entraria em F3.2 (Fornecedores).
- **Escalonamento de Ação parada**: prazo e destino ficam na regra (F13.3). Precisa de campo no modelo de regra.
- **Notificação em nome de quem?** Ação sem dono (ex.: estoque baixo): vai para todos com a permissão; quem resolver encerra para todos. Registrar quem resolveu na auditoria.

## 6. Pontos a decidir

- A organização pode **renomear ou recolorir** áreas? (U6, em aberto.)
- Ação "Comanda aberta há muito tempo" ou "Caixa aberto além da janela": são **Ação** ou **Estado**? Proposta: Estado na tela da comanda e Ação na central só depois do limite de escalonamento.
- Aviso de **venda concluída** em tempo real para o dono: ligado por padrão ou só resumo?
- **Som** ou vibração para Ação crítica no salão? (Fica para o celular.)
