# Novo sistema — UX e design (rascunho vivo)

Status: **em construção.** Este documento registra o que foi decidido durante a prototipação em `prototipos/index.html`. Ainda não é a especificação final de UX: o que não está aqui não foi decidido.

## U1. Princípios

1. **Rápido e prático em lançar.** Abrir comanda, criar e adicionar itens precisa ser muito rápido no celular e no computador. Metas: abrir comanda e lançar o primeiro item em menos de 10 s; cada item a mais em no máximo 2 toques (celular) ou uma digitação (computador).
2. **Simples e neutro.** Interface universal, sem tema "de bar" (nada rústico, madeira ou serifado). Poucos elementos, hierarquia clara.
3. **Preparado para tudo, configurável** (D17): tema claro e escuro, dispositivos variados, recursos ligados por organização e unidade.
4. **Cor nunca é o único sinal.** Todo status e toda notificação têm texto e/ou ícone além da cor.

## U2. Direção visual (decidida na prototipação)

- **Neutro e limpo**: fundo cinza quase branco, cards brancos com borda fina, texto quase preto, uma cor de marca calma (azul no protótipo). No escuro, cinza neutro, sem tom marrom.
- **Bem arredondado**: cards com cerca de 16 px, botões, filtros e itens de menu em formato de pílula.
- **Uma família sem serifa** (Geist no protótipo) para tudo, incluindo títulos e números grandes, com algarismos tabulares.
- **Status** com marcador e palavra; paleta suave que combine com a fonte e a marca (alerta em rosa-carmim, não vermelho puro; destaque de atualização frio, nunca amarelo).
- Tudo parametrizado por tokens (cor, fonte, arredondamento, densidade, tamanho de texto, limite de alerta), alterável ao vivo pela bolinha de configuração do protótipo. A combinação final sai de "Copiar configuração".
- **Rejeitado:** direções A (escura e densa), B (clara com serifa e creme), C (teclas coloridas), terracota e fundo creme.

## U3. Estrutura de navegação

- **Menu lateral recolhível.** Aberto: ícone e nome. Recolhido: só ícones com selo de contagem; ao clicar, focar ou passar o mouse aparece um pop-up com o nome e a quantidade de notificações.
- **Trilha de navegação em toda tela** com "›" (ex.: `Operação › Comandas › Mesa 12`), com todos os níveis clicáveis menos o último.
- **Registro central de telas** (`FLOW_SCREENS`: id, título, tela-pai, rota, ícone, palavras-chave). Dele saem o menu, a trilha e, no futuro, a **busca de telas** e um **assistente de IA que ajuda o usuário a se mover** pelo sistema.

## U4. Lançamento rápido (comandas)

Em avaliação no protótipo; decisões abaixo são a hipótese inicial.

**Computador**
- **Barra de lançamento rápido:** "3 chope" + Enter; autocomplete; código curto por produto; foco permanece para o próximo item.
- **Teclado:** `N` nova comanda (mesa ou nome), Enter e cai na barra; `L` abre o lançamento flutuante.
- **Lançar sem abrir a comanda:** botão "+" no card.
- **Como a comanda abre** (a testar, trocável no protótipo): painel lateral só para consulta (padrão, hipótese), janela grande no centro, ou tela cheia com trilha. O painel lateral que empurra a lista foi **rejeitado para lançar**.
- **Fechar conta rápido:** serviço pré-aplicado, forma de pagamento em um toque, divisão igual em um toque, pagamento parcial.

**Celular (garçom)**
- Nova comanda abre **teclado numérico** grande: "12" + OK já entra no lançamento.
- Frequentes por horário e pela última comanda da mesa; repetir rodada; observação em um toque.
- **Envio sem confirmação, com desfazer** por alguns segundos (a confirmação antes de enviar é opção). Pergunta ainda aberta com o dono do restaurante.

## U5. Bolinhas flutuantes

Presentes em **qualquer tela**, empilhadas no canto inferior (no celular, acima da barra de ações, menores). Nunca se sobrepõem, são acessíveis por teclado, só um painel aberto por vez.

| Bolinha | Função | No produto real |
|---|---|---|
| **Lançar** | Lançar itens em qualquer comanda de qualquer tela, sem navegar | Sim |
| **Notificações** | Uma por grupo, com cor e contagem próprias (ver `02-features` F13, "bolinhas flutuantes") | Sim |
| **Configuração** | Mudar cores, fonte, forma, regras e visualização ao vivo | **Não**: só no protótipo, marcada como tal |

**No celular** (dono delegou a escolha): a notificação vira **uma bolinha só**, com o total e pontinhos coloridos por grupo; o painel tem abas por grupo; a de lançar fica separada; no máximo duas bolinhas visíveis, acima da barra de ações. No computador, uma bolinha por grupo.

**Notificações (direção do dono):** duas famílias, **plataforma** (menos urgente) e **operação** (ações para fazer e avisos, como "estoque de carne acabando" ou "comanda fechada"); uma bolinha por grupo com número; cores propostas laranja (ação), azul (informativo), verde (positivo); o que cada usuário vê depende da **permissão**. Detalhe completo e pontos em aberto em `02-features` F13.

## U6. Pendências deste documento

- Escolha final de como a comanda abre (painel, janela, tela cheia) depois de testar.
- Significado final das cores das bolinhas de notificação e se a organização pode renomear ou recolorir grupos.
- Respostas do dono do restaurante: luz baixa ou normal, densidade, entrada de quantidade, confirmar antes de enviar.
- Telas ainda não prototipadas: mesas, cardápio, estoque, caixa, financeiro, relatórios, Control Tower.
- Catálogo de componentes e design system (começo do trabalho foi interrompido; ver prototipação).
- Acessibilidade: contraste foi calculado, mas falta teste com leitor de tela e em aparelho real.
- Fonte e paleta finais, depois de uso real da bolinha de configuração.
