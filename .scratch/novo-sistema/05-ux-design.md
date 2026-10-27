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
- **O menu é filtrado pela permissão de cada tela**, mesmo que a tela ainda não esteja prototipada. O protótipo cobre os oito perfis padrão (`02d`); o que cada um enxerga:

| Perfil | Menu lateral (web) |
|---|---|
| **Dono** | Operação (Dashboard, Comandas, Cozinha e bar, Mesas, Cardápio, Clientes, Calendário) · Gestão (Estoque, Compras e fornecedores, Caixa, Financeiro, Relatórios, Equipe e comissões, Auditoria) · **Administração (Usuários, Perfis e permissões, Configurações, Organização e unidades)** |
| **Gerente** | Igual ao dono, **sem** Organização e unidades. Em Perfis só consulta; Configurações é só da unidade |
| **Caixa** | Dashboard, Comandas, Mesas, Cardápio, Clientes, Calendário, Caixa |
| **Garçom** | Comandas, Cozinha e bar, Mesas, Cardápio, Clientes, Calendário (o dia a dia é no celular) |
| **Estoquista** | Estoque, Compras e fornecedores, Relatórios, Cardápio, Calendário |
| **Financeiro** | Dashboard, Financeiro, Caixa, Relatórios, Equipe e comissões, Estoque, Compras e fornecedores, Cardápio, Clientes, Calendário |
| **Cozinha e bar** | Cozinha e bar, Mesas, Estoque, Cardápio, Calendário |
| **Somente leitura** | Tudo de consulta (Operação e Gestão), sem Administração |

- **Celular:** o app é do garçom e também gerencia (aba Mais, U7c). Ele mostra só as **próprias comandas** (`comanda:ver_comandas_de_outros` é de D, G, C e R). Quem tem essa permissão abre direto em "Todas"; perfis sem `comanda:criar` não veem "Nova comanda"; perfis sem `comanda:ver` (cozinha, estoque, financeiro) recebem um aviso de que usam a versão web.

## U3b. Lista de comandas: status, ordenação e fixadas

- **Selo do cartão = etapa da cozinha** (`em preparo`, `pronto`, `entregue`), só quando a unidade usa cozinha. Status da comanda (`aguardando pagamento`, `paga`) fica no rodapé do cartão, ao lado de "Fechar conta" (alinhado à direita, espelhando o selo do topo). "Em alerta" **não é status**: é só o tempo parado, em texto de cor de alerta, mais o filtro "Em alerta".
- **Número da comanda (#12):** pílula discreta antes do nome em cartão, tabela (coluna **Nº**, ordenável), detalhe, tickets da cozinha e bar e lista de comandas da mesa. Segue a opção "Numeração das comandas" da unidade (reinicia todo dia, ou contínua, aí aparece como #2043). A busca aceita o número.
- **Linha "Último:"** mostra o último item lançado e a hora, para reconhecer a comanda sem abrir. "Fechar conta" fecha direto do cartão.
- **Ordenação (cartões):** mais recentes (última atividade), mais antigas, cozinha primeiro (preparo → pronto → entregue), maior valor, mais tempo aberta. Na tabela, ordena pelo cabeçalho da coluna.
- **Fixar comanda:** alfinete no cartão; fixadas vão sempre ao topo, na ordem escolhida. Fixar é **preferência pessoal**, não muda a comanda para os outros.
- **Persistência:** ordenação (uma por visão, cartões e tabela) e fixadas são salvas **no navegador/aparelho** (`localStorage`), sem custo no banco. No sistema real, opcional sincronizar como preferência leve por usuário (uma linha de JSON), para seguir a pessoa entre aparelhos. Fixadas de comanda já paga saem sozinhas.
- **Botões flutuantes** (comanda rápida e configuração) são translúcidos com desfoque, para não esconder o conteúdo atrás.

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
- **Nova comanda na web:** com **Usar mesas** ligado, a barra mostra as **mesas livres** (número e lugares) para escolher com um toque, sem impedir digitar um nome; desligado, mostra só o campo de nome ou número (número vira "Comanda 7", não "Mesa 7").
- Frequentes por horário e pela última comanda da mesa; repetir rodada; observação em um toque.
- **Envio sem confirmação, com desfazer** por alguns segundos (a confirmação antes de enviar é opção). Pergunta ainda aberta com o dono do restaurante.

## U5. Bolinhas flutuantes

Presentes em **qualquer tela**, empilhadas no canto inferior (no celular, acima da barra de ações, menores). Nunca se sobrepõem, são acessíveis por teclado, só um painel aberto por vez.

| Bolinha | Função | No produto real |
|---|---|---|
| **Lançar** | Lançar itens em qualquer comanda de qualquer tela, sem navegar | Sim |
| **Notificações** | Uma bolinha só (orbe com contador de Ações pendentes). Abre um painel com duas abas, **Ações** e **Avisos** (ver `02f-catalogo-notificacoes`) | Sim |
| **Configuração** | Mudar cores, fonte, forma, regras e visualização ao vivo | **Não**: só no protótipo, marcada como tal |

**Notificações — revisão (decisão do dono):** na **web** o sino deixou de ser bolinha flutuante e virou um **botão azul com sino branco na barra de cima**, ao lado da troca de tema; o contador de Ações pendentes fica no canto e, ao chegar algo, o sino "brilha" (anel que pulsa) e a pílula aparece logo abaixo dele. No **celular** é a aba **Avisos** da barra inferior. O painel segue o modelo de caixa de entrada: abas **Todas, Ações e Avisos**, ícone da área em círculo suave (cor pela gravidade, com a palavra Atenção ou Crítico), botões de resposta (ex.: Confirmar recebimento / Reagendar; Aprovar / Recusar) e um visto para marcar o aviso como lido. **Simplificado depois do teste:** sem arquivar, silenciar nem menu "…"; aviso lido some, ação resolvida some; sem filtros de área nem preferências do usuário (quem define o que cada um recebe é a gestão, ver `02f` §2b). Com algo pendente, o sino emite uma onda suave (mais rápida se houver crítico). A pílula de chegada aparece **dentro da barra de cima**, no lugar da data, sem cobrir a tela. O que cada pessoa vê depende da permissão da área (ex.: garçom só vê Salão e Segurança).

**Versão anterior (substituída acima):** **uma bolinha só** nos dois, com o contador de **Ações pendentes** no canto (ponto azul se só houver Avisos novos). O painel tem duas abas: **Ações** (pede algo, some quando resolvida; marcador âmbar) e **Avisos** (só informa; marcador azul), com chips de área (Estoque, Compras, Financeiro, Caixa, Salão, Segurança, Plataforma). A gravidade (`info`, `atenção`, `crítico`, `concluído`) é um marcador dentro do item, com palavra e ícone, não a cor da aba. Ao chegar uma Ação ou um Aviso crítico, uma **pílula** (etiqueta do tipo, mensagem, seta) aparece ao lado da bolinha por 8 s e leva à aba certa. Estados contínuos (sem conexão, caixa aberto há muito tempo) ficam em faixa na tela, fora da central. No máximo duas bolinhas visíveis: a de lançar comanda e a de notificações.

**Notificações (direção do dono):** duas famílias de origem, **plataforma** (menos urgente) e **operação**; o que cada usuário vê depende da **permissão**. Catálogo completo de avisos, tipos e regras de comportamento em `02f-catalogo-notificacoes.md`; mecânica em `02-features` F13.

## U6. Calendário (F15, protótipo web)

Prototipado em `prototipos/index.html` (`#screen=calendario`), web e celular (ver U7).

- **Quatro visões** (Mês, Semana, Dia, Agenda), setas e "Hoje". Semana começa no domingo.
- **Sem atalhos de teclado no calendário.** Atalhos ficam só nas Comandas (decisão do dono).
- **Criar arrastando**: em Semana e Dia, arrastar o mouse sobre as horas desenha o intervalo (passos de 15 min) e abre o formulário já preenchido; um clique simples cria 1 h. Em Mês, clicar no dia cria.
- **Tarefas** (além de eventos): criadas à mão, com caixa de conclusão no próprio item (mês, semana, agenda, celular); tarefa vencida e aberta mostra marcador "!" e texto "Atrasada". Pagamentos, entregas, promoções e lembretes são automáticos e somente leitura.
- **Cartão ao passar o mouse** sobre um item: camada, título, quando, situação, local, responsáveis e descrição.
- **Agenda em linha do tempo**: cada item tem uma bolinha na trilha contínua (uma linha só do primeiro ao último dia) e se expande no lugar (detalhes, descrição, sugestão de impacto, atalho para a origem), com "Expandir tudo".
- **Camadas como chips** (Eventos, Pagamentos, Entregas, Promoções, Lembretes e avisos), com contagem do período. Cada camada tem **ícone e nome além da cor**; só aparecem as que o perfil pode ver (`calendario:ver_pagamentos` exige também `financeiro:ver`).
- **Itens do sistema são somente leitura**: o clique abre os detalhes e um botão "Abrir em <tela de origem>". Item com situação mostra marcador e palavra (A pagar, Atrasada, Paga, Agendada, Recebida).
- **Eventos próprios**: criar (botão, tecla `E`, ou clicar num dia ou horário vazio), editar, **arrastar para reagendar** (com "Desfazer"), excluir. Excluir é ação auditada e **exige motivo**.
- **Recorrência** com exceções: ao editar ou excluir uma ocorrência pergunta "Só este / Este e os seguintes / Todos". As duas últimas exigem `calendario:evento_recorrente_editar_serie`.
- **Visibilidade por perfil**: sem `calendario:ver_de_outros`, o usuário só vê eventos em que é responsável ou que são da casa (sem responsável). Lembretes dependem da permissão da origem (documentos, estoque).
- **Sugestão de impacto** (F15.11) aparece como aviso azul no evento, só indicação.
- **Limites do protótipo**: eventos e exceções ficam só em memória; sem anexos, convites com confirmação, compartilhamento nem configuração de camadas; o iCal é só uma janela de demonstração. Eventos que viram a meia-noite aparecem no dia de início.

## U7. Celular por perfil (abas inferiores)

O celular deixou de ser só "app do garçom": é um app com **abas inferiores filtradas pela permissão** do perfil.

| Aba | Aparece para | Conteúdo no protótipo |
|---|---|---|
| **Comandas** | quem tem `comanda:ver` | Lista e comanda como antes; barra grande "Nova comanda" |
| **Calendário** | quem tem `calendario:ver` | Mês com pontos por camada + lista do dia, ou Agenda; itens expansíveis; camadas por permissão; criar evento simples (título, data, horário, cor) |
| **Avisos** | todos | Abas Ações e Avisos, chips de área, mesmos dados da web |

- O perfil abre na primeira aba disponível (ex.: estoquista abre em Calendário; sem Comandas).
- **"Nova comanda" fora da aba Comandas** vira o **círculo pequeno** no canto; na aba Comandas fica a barra grande.
- No celular o calendário só cria; **editar, excluir e recorrência ficam na versão web** (decisão provisória).
- A aba Avisos mostra selo com Ações pendentes (ponto azul se só houver Avisos novos).

## U7b. Usuários e perfis (F11, protótipo web)

Duas telas em Administração (`#screen=usuarios`, `#screen=perfis`), com os 210 itens do catálogo `02d` (gerados da tabela). Dados de exemplo em `localStorage`; "Restaurar dados de exemplo" no rodapé.

**Usuários** (`usuario:ver`)
- Resumo: usuários no plano (limite do plano, F12.12), convites pendentes, contas operacionais, sessões ativas. Convidar some do alcance quando o plano enche.
- Lista **espaçosa e paginada** (ver U9) com busca, chips de situação (Todos, Ativos, Pendentes, Desativados), filtro por perfil e por tipo de conta. Linha de cerca de 76 px: avatar, nome, e-mail ou usuário; perfil com o escopo embaixo; situação (marcador + palavra); último acesso; seta. A linha toda abre a ficha.
- **Convidar:** conta com e-mail (link de uso único, mostrado com Copiar) ou operacional (usuário único na organização + credencial temporária **mostrada uma só vez**). Perfis que a pessoa que convida não possui ficam desabilitados ("só se concede o que se possui"); perfil com administração, financeiro ou exportação bloqueia conta operacional.
- **Ficha** em **janela central**, como a comanda (não painel lateral; decisão do dono após o teste). Abas com palavras simples: *Acesso* (perfil, onde pode atuar, permissões extras com motivo), *Permissões* (cada uma como Pode, Pode restrito com limite ou Não pode, com a origem), *Onde entrou* (logado agora, deslogar, aparelhos do PIN), *Avisos* (`02f` §2b), *Histórico*. Textos curtos, sem jargão ("Deslogar de tudo", "Liberar" e "Bloquear" em vez de conceder e negar).
- Ações auditadas pedem motivo: desativar, reativar, redefinir acesso, encerrar sessões, trocar perfil, exceção. Desativar encerra as sessões na hora.
- **Proteções visíveis:** dono não é desativado nem rebaixado; ninguém altera o próprio acesso; só o dono redefine o acesso do dono; sem `permissao:atribuir` a aba Acesso fica travada com o motivo escrito.
- **Aparelhos do salão** (aba, `sessao:ver_todas`): registrar com código de pareamento, revogar.

**Perfis e permissões** (`perfil:ver`)
- Lista dos 8 padrão (travados, só duplicam) e dos personalizados, com pessoas e nº de permissões. Detalhe agrupado por módulo, com busca, "só as concedidas", marcadores **Auditada** e **Restrita** e campo de limite nas permissões `[L]` (desconto: 50 / 20 / 5 / 0 % por perfil, os demais limites seguem em aberto, `02d` §5).
- **Novo perfil / Duplicar:** partir de um modelo ou em branco; escolha obrigatória entre **acompanhar o modelo** (vinculado, travado até desvincular) e **copiar e editar**.
- Edição em rascunho: barra fixa com nº de alterações, quantas pessoas afeta e motivo obrigatório. Excluir só sem pessoas usando. Histórico das alterações do perfil.
- **Matriz comparativa:** todos os perfis lado a lado (● concedida, ◐ restrita, – não concede), com busca.
- O Gerente consulta, mas não cria nem edita (`perfil:gerenciar` é do dono).

Achados ao prototipar: (1) `comanda:fechar_cego` é só do Caixa, então o Gerente, que não a possui, **não consegue conceder o perfil Caixa** pela regra "só se concede o que se possui"; decidir se o Gerente ganha essa permissão ou se ela sai da regra. (2) O Dono tem 209 de 210 pela mesma razão (fechamento cego é só do Caixa por desenho). (3) Limites padrão de cortesia, sangria, estorno e consumo interno continuam sem valor.

## U9. Paginação de listas (padrão)

Toda lista que pode crescer (usuários, comandas fechadas, compras, auditoria, clientes) é **paginada no servidor**. Referência: Usuários no protótipo.

- **Rodapé da lista:** "Mostrando 11 a 15 de 15" à esquerda, seletor **Por página** (10, 25, 50; padrão 10, e 25 em telas de consulta como auditoria) e o componente `.pager`: Anterior, números com reticências (primeira, última, atual e vizinhas), Próxima. A página atual leva `aria-current="page"`; Anterior e Próxima desabilitam nas pontas.
- **Filtro e busca voltam para a página 1.** Mudar o tamanho da página também. Se a página atual deixar de existir (ex.: desativou o último da página), vai para a última.
- **Total sempre visível**, mesmo com filtro: os chips de situação mostram a contagem de cada filtro, vinda do servidor.
- **API (`04`):** `GET /usuarios?page=2&size=10&q=&situacao=&perfil=&conta=&ordem=`, resposta com `itens`, `total`, `pagina`, `tamanho`. Tamanho máximo rígido de 100. Ordenação estável (desempate por id) para a página não repetir nem pular linha. Listas de milhões de linhas (auditoria, movimentos) usam **cursor** em vez de número de página; a tela troca os números por Anterior e Próxima.
- **Estado na URL:** página, tamanho, busca e filtros no endereço, para voltar e compartilhar. O tamanho escolhido fica no aparelho (`localStorage`), por lista.
- **Foco e leitura:** ao trocar de página o foco vai para a página atual; a lista anuncia "Página 2 de 2" para leitor de tela; a rolagem volta ao topo da lista.
- **Estados:** carregando mostra linhas esqueleto do mesmo tamanho (sem pulo); vazio mostra "Ninguém encontrado" com "Limpar filtros"; erro mostra banner com "Tentar novamente" sem perder a página.
- **Celular:** listas viram cartões e usam "Carregar mais" (rolagem incremental), não números.
- No protótipo: Usuários tem 10 por página com 15 pessoas, para a segunda página aparecer. Matriz de permissões e lista de perfis não paginam (tamanho fixo e pequeno).

## U10. Unidades: seletor e configurações (F14, protótipo web)

**Seletor de unidade** = o botão no topo do menu lateral (nome da organização + unidade atual).
- Quem tem acesso a **uma** unidade vê o botão fixo, sem seta. Quem tem acesso a **mais de uma** vê a seta e, ao clicar, um menu com as unidades do **seu escopo** (endereço curto e nº de pessoas), a opção **Todas as unidades** (só para quem tem escopo da organização inteira e há mais de uma unidade) e o atalho "Gerenciar unidades" (só para quem vê a tela Organização).
- A escolha vale para o sistema todo (comandas, caixa, estoque, configurações), fica salva no aparelho e vem preenchida na próxima entrada. A linha "Turno noite, Unidade X" das Comandas acompanha. Teclado: Esc fecha, setas navegam. A unidade vem sempre do vínculo da pessoa no servidor; o seletor só escolhe entre as permitidas (`03` S5).
- **Todas as unidades** é visão de consulta: nas Configurações vira uma **comparação** lado a lado (organização e cada unidade), com destaque no que difere do padrão e filtro "Só o que é diferente". Para editar, escolhe-se uma unidade (também por botões na própria tela).
- No protótipo só as configurações mudam entre unidades; as comandas de exemplo são as mesmas.

**Configurações: um só lugar, em árvore** (`configuracao:ver` e/ou `organizacao:configurar`). O item "Organização e unidades" saiu do menu; tudo mora em **Administração › Configurações**, com uma **barra lateral em árvore** (sem abas):
- **Organização · Bar do Zé** (só dono): Dados da empresa, Unidades, Padrões e travas, Segurança, Plano.
- **Unidades**: cada unidade do escopo da pessoa; a aberta mostra suas seções por baixo (Cadastro, Operação, Mesas e áreas, Venda, Caixa, Cozinha e bar, Estoque, Histórico). **Comparar unidades** no fim, para quem tem escopo da organização.
- **O escopo está sempre no título:** "Organização · Padrões e travas", "Unidade Praia · Venda". Cada regra diz a origem (herdada, própria, travada), então não há dúvida de onde se está nem de quem manda.
- **Entrar numa unidade abre um resumo em cards**, um por seção, com o valor em uso e quantas opções são próprias; clicar abre a seção (ou usar a árvore). Em pequeno, a árvore vira linha de botões acima do conteúdo.
- Escolher uma unidade na árvore também troca a unidade do menu (mesmo contexto do sistema). Quem tem uma só unidade não vê a lista de unidades trocando nada.
- **Horário de funcionamento** (seção da unidade, F5.8 e F5.9): card **Hoje** (aberto das X às Y, e de onde vem a regra) com **Estender hoje**; **Semana padrão**, uma linha por dia com chave Aberto/Fechado e horário próprio (vazio usa o horário padrão da unidade, definido em Operação); **Exceções por data** (feriado fechado, horário especial, dia estendido, sempre com motivo); e **Fora do horário** (só avisar, bloquear comandas novas, ou bloquear tudo menos fechar a conta; tolerância em minutos; liberar com permissão). Cada mudança entra no Histórico. No protótipo a regra é salva, mas o bloqueio ainda não é aplicado nas telas.
- A **herança com travas continua**: organização define o padrão, a unidade sobrescreve o que não estiver travado.

**Seções da unidade.** Cadastro (nome, endereço, CNPJ, telefone, fuso) é só da unidade; as demais são **em camadas**. *Mesas e áreas* é o cadastro de mesas (a tela Mesas é só operação).
- Cada linha diz de onde vem o valor: **Herdado da organização**, **Definido nesta unidade** (com "Voltar ao padrão (valor)") ou **Travado pela organização** (campo desabilitado, mostra o valor da organização). Linhas alteradas ficam realçadas; resumo no topo (quantas opções próprias, quantas travadas).
- Sem `configuracao:unidade_editar` a tela é só leitura, com aviso. Gerente edita a própria unidade; dono edita qualquer uma.
- **Salvar:** barra fixa com nº de alterações; valor inválido bloqueia e mostra o motivo na própria barra (taxa 0 a 20%, abertura diferente de fechamento). Ao salvar pede **motivo** e, se houver opção com efeito operacional (taxa, couvert, horário, estoque negativo), pergunta **quando começa a valer**: agora ou na abertura do próximo dia (F14.3).
- **Histórico** (`configuracao:ver_historico`): quem, quando, antes e depois, motivo; **Restaurar valor anterior** (`configuracao:restaurar`, só dono) cria nova entrada, não apaga a antiga.

**Organização** (`organizacao:configurar`, só dono; parte da mesma tela de Configurações)
- *Dados da empresa* (razão social, fantasia, CNPJ, contato, logotipo), *Unidades*, *Padrões e travas*, *Segurança*, *Plano*.
- **Unidades:** lista com pessoas, opções próprias e situação; Abrir configuração (leva ao resumo da unidade), Desativar (pede motivo; bloqueia se for a última ativa) e Reativar; **Nova unidade** pergunta se começa só com o padrão ou **copiando a configuração** de outra (F14.6) e respeita o limite do plano (3 no protótipo). Acesso à unidade nova é dado em Usuários.
- **Padrões e travas:** o valor padrão de cada opção e a chave **Unidades podem mudar / Travada** (`configuracao:travar_opcao`). Mostra quantas unidades já têm valor próprio; ao travar, esses valores passam a ser ignorados (e voltam se destravar).
- **Segurança:** tamanho mínimo da senha, sessão sem uso, tamanho do PIN, Google; 2FA aparece desligado ("em breve").
- **Plano:** só leitura (plano, unidades, usuários, próxima fatura).

Pontos abertos: (1) "Todas as unidades" para quem tem escopo parcial de 2 ou mais unidades (hoje só vê o seletor entre as suas, sem comparar). (2) Dados por unidade no protótipo (comandas, caixa, estoque) para o seletor mostrar efeito real. (3) Fiscal e Cozinha (praças, impressoras) ainda sem tela. (4) Pedir confirmação ao trocar de unidade com uma comanda aberta ou rascunho pendente.

## U11. Cozinha e bar e Mesas (F6, F1.16, protótipo web)

**Cozinha e bar** (`cozinha:ver_fila`)
- Quadro com três colunas: **Novos**, **Em preparo**, **Prontos para servir**. Cada **pedido de preparo** é um cartão: comanda, garçom, hora, tempo desde o lançamento, praça (Cozinha ou Bar) e os itens. Um pedido junta os itens da mesma comanda e mesma praça lançados no mesmo minuto. Ordem: do mais antigo para o mais novo.
- Filtro por **praça** (Todas, Cozinha, Bar) com contagem. Resumo de atrasados: passou do **tempo-alvo** da unidade (Configurações › Cozinha e bar, padrão 15 min; 20 min no exemplo). Atrasado = borda e palavra "Atrasado" (nunca só cor).
- Ações: **Começar** (`cozinha:marcar_preparo`), **Marcar pronto** (`cozinha:marcar_pronto`; o garçom da comanda é avisado), **Entregue**, **Voltar** (desfaz um passo) e **Reenviar** (`cozinha:reenviar_pedido`). Quem só tem `ver_fila` (garçom) acompanha e marca **Entregue**; quem não tem permissão não vê o botão.
- **Item cancelado depois de enviado** continua no cartão, riscado e com "Cancelado", até alguém clicar **Ciente** (avisa a praça, F6).
- **Fonte única:** o estado do item na comanda (`novo → em preparo → pronto → entregue`). O selo "Em preparo, Pronto, Entregue" nas comandas é o menor estado entre os itens, então as duas telas nunca divergem. Item lançado nasce "novo". A contagem do menu lateral é de pedidos ainda não prontos.
- Se a unidade desliga a fila de preparo (Configurações), a tela explica e leva às configurações; em "Todas as unidades", pede para escolher uma.

**Mesas** (`mesa:ver`)
- **Mapa por área** (Salão, Varanda). Cada mesa mostra número, lugares, situação em palavra (**Livre, Ocupada, Aguardando conta, Reservada**), tempo de ocupação, consumo e avisos ("Pedido pronto", "Parada"). A situação é **calculada das comandas abertas**, nada é digitado no mapa. Resumo no topo (livres, ocupadas e % do salão, aguardando conta, reservadas com horário); chips de situação esmaecem as demais mesas; filtro por área.
- Clicar numa mesa abre o detalhe ao lado: comandas dela (com tempo, valor e etapa da cozinha), **Abrir** a comanda, **Outra comanda nesta mesa** (várias comandas por mesa, F1), **Abrir comanda** em mesa livre ou da reserva, e **Mudar de mesa** (`comanda:transferir_mesa`): o mapa destaca só as mesas livres e a comanda passa a ser da escolhida.
- Quem não tem `comanda:ver` (cozinha e bar) vê situação e tempo, sem valores nem comandas. Garçom vê só as próprias comandas no detalhe; as de outros aparecem como "De outro garçom".
- **Cadastro fica em Configurações da unidade, aba "Mesas e áreas"**, não na tela Mesas (que é só operação; quem tem `mesa:gerenciar` vê o atalho **Gerenciar mesas**). Lá: lista por área com número (único), lugares e área editáveis; **Nova mesa**, **Nova área**, **Desativar mesa** (só se estiver livre, pede motivo). O cadastro é por unidade; unidade nova começa sem mesas. **Sem desenhar o mapa na v1:** as mesas aparecem em grade por área, em ordem de número; arrastar para posicionar fica como evolução.
- Se a unidade desliga o uso de mesas, a tela explica e leva às configurações.

Pontos abertos: (1) quem marca **Entregue**: hoje quem marca pronto ou lança item; talvez precise de permissão própria (`cozinha:marcar_entregue`). (2) Cancelar item enviado deve exigir `ciente` da praça antes de sumir do quadro? (3) Reserva no protótipo é um dado da mesa; reservas de verdade (lista, horário, aviso) não estão no escopo ainda. (4) Praça por produto: no protótipo, Porções e Pratos vão para a Cozinha e o resto para o Bar; falta a tela de configurar praças (`cozinha:configurar_pracas`). (5) Tela cheia para monitor de cozinha, com texto maior e som de pedido novo.

## U7c. Celular que também gerencia: aba "Mais" (protótipo)

Decisão do dono: o celular não é só lançamento. Quem gerencia precisa ver o que acontece no salão pelo aparelho, sem levar toda a administração para a tela pequena.

- **Quarta aba "Mais"** (ícone de três pontos), depois de Comandas, Calendário e Avisos. Mesma regra de sempre: cada perfil vê só o que tem permissão. Quem não tem `comanda:ver` e tem `cozinha:ver_fila` (cozinha e bar) **abre direto na fila de preparo**.
- **Mais, "Agora":** cartões com os números do momento, cada um abre a tela: **mesas ocupadas** (ex.: 7 de 18, 2 aguardando conta), **comandas abertas** (e quantas paradas; garçom vê só as dele), **pedidos na cozinha e bar** (quantos atrasados e prontos esperando) e **consumo em aberto** (só com `comanda:ver_comandas_de_outros`). Cartão sem permissão ou de recurso desligado na unidade não aparece.
- **Mais, "Telas":** Mesas e Cozinha e bar (com selo de pedidos na fila). **"Em breve no celular":** Caixa, Estoque, Financeiro, Relatórios, Cardápio, Clientes, Equipe e Auditoria aparecem travados e só para quem tem a permissão, com "Na versão web". **Usuários, perfis, configurações e organização não vêm para o celular**; o rodapé da tela avisa.
- **Mesas no celular:** chips de situação com contagem (Todas, Livres, Ocupadas, Conta, Reservas) e de área; mesas em duas colunas, com número, lugares, situação em palavra, tempo, consumo (só com `comanda:ver`) e "Pedido pronto" ou "Parada". Tocar abre uma folha com as comandas da mesa (**Abrir**) ou **Abrir comanda** em mesa livre. Só consulta: editar o mapa fica na web.
- **Cozinha e bar no celular:** chips de praça e abas de etapa (**Novos, Em preparo, Prontos**) com contagem; cartões grandes com botão de largura total (**Começar, Marcar pronto, Entregue**, **Voltar**), mesmas permissões e mesmo tempo-alvo da web; atrasado com borda e palavra. Item cancelado depois de enviado pede **Ciente**.
- **Troca de unidade:** a linha "Bar do Zé · Unidade X" do cabeçalho (Comandas, Calendário, Mais e subtelas) vira botão com seta para quem tem mais de uma unidade; abre uma folha com as unidades do escopo e "Todas as unidades" (só escopo da organização). Quem tem uma unidade vê texto fixo. A escolha é a mesma da web (mesmo aparelho): configurações ligadas ou desligadas (fila de preparo, mesas) e o mapa de mesas seguem a unidade.
- Ao tocar em Mais de novo, volta ao menu (sai da subtela). O botão redondo de nova comanda não aparece em Mais.

Pontos abertos: (1) dados por unidade (hoje as comandas de exemplo são as mesmas em qualquer unidade e diferem entre web e celular). (2) "Todas as unidades" no celular só mostra números de uma unidade. (3) Quais das telas "em breve" entram primeiro: caixa (abertura e fechamento) e alertas de estoque são os candidatos naturais. (4) Notificação "pedido pronto" deve abrir a comanda ou a mesa.

## U12. Cardápio (F2, protótipo web)

Duas abas: **Produtos**, **Categorias**. Promoções e combos têm tela própria (U12b). Preço e "acabou" seguem a **unidade do seletor** (U10); dados do produto, categoria, opções e ficha técnica são da organização.

**Produtos** (`cardapio:ver`)
- Lista paginada (U9) com resumo (ativos, acabou agora, sem ficha técnica, margem média só com `cardapio:custo_ver`, senão promoções ativas), busca por nome ou **código curto**, chips de situação (Todos, Disponíveis, Acabou, Inativos, Sem ficha), categoria em dois níveis (inclui "Tudo de Bebidas") e ordem (nome, código, preço, margem).
- Colunas: produto (iniciais, nome, detalhe, código), categoria e subcategoria, **preço** (da unidade, com "Preço desta unidade" e, se há promoção ativa, preço promocional com o antigo riscado), **situação** (Disponível, Acabou, Acabou em N de M unidades, Inativo), **pode produzir** (da ficha; "Sem ficha"; vermelho abaixo de 10) e **margem** (só com `custo_ver`; vermelho abaixo de 30%).
- **Acabou / Voltou** na própria linha (`cardapio:marcar_indisponivel`, que inclui garçom, caixa, estoquista e cozinha): vale para a unidade escolhida e fica no histórico. Em "Todas as unidades" pede para escolher uma.
- **Janela do produto** (central, como a ficha de usuário), **quatro abas**: *Produto*, *Opções*, *Ficha técnica*, *Histórico*.
  - ***Produto*** reúne tudo o que se mexe no dia a dia, em três blocos: **dados** (nome, detalhe, categoria, código curto, praça, foto, descrição), **preço** (preço de venda e, ao lado, **custo pela ficha, lucro por venda e margem**, recalculados ao digitar; sem ficha, convida a adicionar; promoções que atingem o produto numa linha, com atalho) e **onde vende**. No fim, a chave Produto ativo.
  - ***Onde vende***: uma linha por unidade, com **preço próprio** (vazio usa o preço de venda; mostra a margem naquela unidade) e **uma única escolha de situação: Disponível, Acabou ou Não vende aqui**. Substitui as antigas seções separadas de preço por unidade e disponibilidade. "Não vende aqui" tira o produto do lançamento daquela unidade (é o cardápio por unidade); "Acabou" é temporário.
  - *Opções* (grupos de adicionais com mínimo, máximo, obrigatório e preço extra). *Ficha técnica* (insumos, quantidade, perda no preparo, custo, estoque; produção possível; **acabar sozinho quando o insumo zerar** passou para cá, junto da ficha). *Histórico*.
  - Produto inativo vale para todas as unidades; para uma só, usa-se "Não vende aqui".
- **Salvar:** barra fixa com o motivo do bloqueio ou "Alterações não salvas"; fechar com alterações pergunta. Mudar **preço** ou **ficha** pede motivo (auditadas). **Limite de variação de preço** (`preco_alterar` é `[L]`): dono sem limite, gerente até 25% por alteração (sugestão; valor padrão ainda é decisão do dono).
- Validações: nome, preço entre R$ 0,01 e 9.999, código único, grupo com mínimo maior que máximo, quantidade de insumo maior que zero.
- Sem `produto_editar`, tudo fica só leitura com aviso; sem `ficha_ver`, a aba Ficha some; sem `custo_ver`, somem custo, margem e a ordem por margem.

**Categorias** (`produto_editar`): árvore de dois níveis com nº de produtos e situação; nova categoria, nova subcategoria, renomear, desativar e ativar (pede motivo; tira os produtos do lançamento sem apagar).

**Promoções** saíram do Cardápio: a ficha do produto lista as promoções dele e leva à tela **Promoções** (U12b). O preço promocional continua riscado na lista.

## U12b. Promoções e combos (F2.4, F2.5, F2.12, protótipo web)

Item **Promoções** em **Gestão** (só quem gerencia promoções ou combos). Uma lista só, com resumo (Ativas agora, Agendadas, Fora da janela, Desativadas ou encerradas), chips de **tipo** (Todas, Promoções, Combos) e de **situação**, e colunas: nome (com os produtos), tipo, oferta (15% ou R$ 44,00, com "economiza R$ x" no combo), **quando e onde** e situação. Botões **Nova promoção** e **Novo combo**.

**Cadastro em janela larga, duas colunas.**
- **Esquerda:** nome → **desconto** (percentual ou valor fixo) ou **preço do combo** → **Quando vale** → **Onde vale**.
  - *Quando vale* começa por dois cartões: **Pontual** ("um dia ou um período, uma vez só") e **Recorrente** ("repete nos dias da semana"). Pontual: um dia específico ou de uma data até outra. Recorrente: dias da semana (com atalhos), começa em (opcional) e **Termina**: continua sem data final, ou termina em uma data. **Horário** nas duas: o dia todo ou só um intervalo.
  - *Onde vale*: chips de unidades, todas marcadas por padrão.
- **Direita:** os produtos, com busca, filtro de categoria, contagem, **Marcar os que aparecem** e **Limpar**. Em promoção, caixa de marcar por produto e **preço antigo riscado com o novo** na hora; em combo, **quantidade** por produto.
- **Resumo em frase** fixo no pé ("20% de desconto em 5 produtos · Recorrente · seg a sex · das 17:00 às 19:00 · sem data final · Todas as unidades"); erros claros (nome, desconto, nenhum produto, data final antes da inicial, nenhum dia marcado, horário incompleto).

Pontos abertos: (1) prioridade e empilhamento (hoje vale a primeira). (2) Combos com escolhas e aparição no lançamento da comanda. (3) Recorrência por dia do mês. (4) "Leve X pague Y". (5) Resultado da promoção (vendas e margem) na própria tela, ligado a Relatórios.

Pontos abertos: (1) **Combos** (F2.5, `combo_gerenciar`) ainda sem tela. (2) Comandas da web e do celular ainda usam a própria lista de produtos de exemplo, não este cadastro; falta ligar lançamento, praça da cozinha e "acabou" ao cardápio. (3) Sub-receitas, conversão de unidade e fator de correção da ficha (F2.6) ficaram de fora. (4) Visão mensal das promoções (F2.12). (5) Cardápio digital por QR e foto de verdade. (6) Limite padrão de variação de preço por perfil.

## U13. Estoque (F3, protótipo web)

Três abas: **Insumos**, **Movimentações**, **Inventário**. O saldo é **por unidade** (seletor do menu, U10); em "Todas as unidades" a lista soma e mostra o saldo de cada unidade, e os botões de movimentar pedem para escolher uma. O mesmo saldo alimenta a ficha técnica do Cardápio: "pode produzir" e "acabou sozinho" mudam na hora (ex.: água tônica zerada faz o gin tônica aparecer como Acabou). O selo do menu lateral conta insumos abaixo do mínimo ou zerados.

**Insumos** (`estoque:ver`)
- Resumo: controlados, abaixo do mínimo, zerados, valor em estoque (só com `estoque:ver_custo`; senão, movimentações do dia). Lista paginada (U9) com busca, categoria, chips (Em dia, Abaixo do mínimo, Zerados, Inativos) e ordem (nome, mais baixos primeiro, movimento recente, maior valor).
- Colunas: insumo (categoria e local), **em estoque** (em kg ou L quando passa de mil), **situação** em palavra (Em dia, Abaixo do mínimo com barrinha, Zerado, Negativo), mínimo, custo médio e valor (só com `ver_custo`; custo exibido por kg ou L), último movimento e **Movimentar**.
- **Janela do insumo:** *Resumo* (saldo, custo médio, **quanto dura** no ritmo das vendas dos últimos 3 dias, últimas movimentações), *Extrato* (tudo, com quem, quando e por quê), *Cadastro* (nome, categoria, local, EAN, alerta de mínimo, ativo; mostra em quais produtos o insumo é usado e quantas unidades dá para fazer com o saldo). Botões de ação no topo conforme a permissão.
- **Movimentos** (todos pedem motivo, ficam no extrato e não são apagáveis):
  - **Entrada manual** (`entrada_manual`): quantidade, custo opcional (**recalcula o custo médio ponderado**) e motivo; compra com nota é na tela Compras e fornecedores.
  - **Perda** (`registrar_perda`): quebra, vencimento, desperdício ou outro.
  - **Consumo interno** (`consumo_interno`, com limite por lançamento): consumidor opcional e observação; categoria própria nos relatórios.
  - **Ajustar saldo** (`ajustar`, com limite de valor): informa o saldo correto e a diferença vira ajuste; estoquista até R$ 100 por vez (sugestão), gerente e dono sem limite.
  - **Transferir** (`transferir`, só com 2 ou mais unidades): saída aqui e entrada lá no mesmo momento.
- **Estoque negativo** segue a política da unidade (Configurações › Estoque): bloqueado, recusa a saída com a explicação. **Alerta** só na transição (cruzou o mínimo), em aviso na tela.
- **Novo insumo** (`insumo:criar`): nome, unidade (un, g, ml), categoria, mínimo, custo e saldo inicial; já fica disponível nas fichas técnicas.

**Movimentações:** extrato geral paginado com busca por insumo e filtro de tipo (Entradas, Vendas, Perdas, Consumo interno, Ajustes, Transferências); valor só com `ver_custo`.

**Inventário (contagem)** (`inventario_contar`): **Nova contagem** lista os insumos controlados; quem não aprova faz **contagem cega** (o saldo do sistema não aparece). Sem divergência, a contagem fecha sozinha. Com divergência vai para **Aguardando aprovação**; quem tem `inventario_aprovar` vê sistema contra contado, **Aprova** (cada diferença vira um ajuste de inventário com o nome de quem aprovou) ou **Recusa** (nenhum ajuste, com motivo). **Quem contou não aprova a própria contagem** (o dono pode, como autoaprovação de negócio pequeno).

Pontos abertos: (1) Fornecedores, Compras e NF-e estão em Compras e fornecedores (U15); receber uma compra dá entrada aqui como `entrada_compra`. (2) Lotes e validade, reserva de saldo no lançamento (F3.12) e conversão de embalagem ficaram de fora. (3) A baixa por venda do protótipo está só no extrato de exemplo; as comandas ainda não baixam estoque. (4) Alerta por push, e-mail e a regra de "sem movimentação". (5) Limites padrão de ajuste e consumo interno por perfil (`02d` §5.3).

## U14. Clientes (F16, protótipo web)

Cadastro **da organização**, igual em todas as unidades. Três abas: **Clientes**, **Fiado** (só com `cliente:ver_historico`) e **Possíveis duplicados** (só com `cliente:mesclar`, com contador). Ninguém precisa estar cadastrado para comprar; o texto do topo diz isso, e o cadastro entra quando há motivo (fiado, nota com CPF, reserva).

**Lista** (`cliente:ver`): resumo (cadastrados, vieram em 30 dias, aniversariantes do mês, fiado em aberto; os que dependem de permissão mostram "–"), busca por nome, telefone, e-mail e documento (este só com dados pessoais), chips (Todos, Com fiado em aberto, Aniversariantes do mês, VIP, Sem vir há 60 dias), etiqueta e ordem. Paginada no servidor (U9). Sem `ver_historico` somem as colunas de visitas e gasto, o fiado e a ordem por gasto.

**Ficha** (janela central, abas): *Resumo* (visitas, gasto, ticket médio, favoritos, observações), *Histórico* (comandas), *Fiado*, *Cadastro* (nome, telefone, e-mail, documento, nascimento, etiquetas, observações; salva ao sair do campo e registra a mudança), *Privacidade*.
- **Dados pessoais:** documento e nascimento aparecem mascarados (`•••.•••.•••-15`) sem `cliente:ver_dados_pessoais`, e o aniversário some da lista.
- **Duplicidade:** telefone repetido é recusado na criação e na edição ("Já existe cadastro com este telefone: Dona Maria"). Quem escapou aparece em *Possíveis duplicados* (mesmo telefone ou documento) com **Mesclar** (escolhe qual fica, motivo obrigatório, une histórico, fiado, etiquetas e consentimentos) e "Não são a mesma pessoa".
- **Consentimento (LGPD):** três finalidades separadas (Atendimento, Comunicação, Fidelidade; esta última ainda sem programa por trás), cada uma com interruptor, data e **registro de alterações** como comprovação. Retirar vale na hora.
- **Direitos do titular:** *Exportar dados* (`cliente:exportar`, gera JSON) e *Anonimizar* (`cliente:anonimizar`, motivo obrigatório; apaga identificação e mantém só totais; **recusa se houver fiado em aberto**).

**Fiado (opcional, D17):** o aviso no topo da aba diz quando está **desligado** na unidade (Configurações › Venda › Permitir fiado, que vem desligado) e que os saldos abertos continuam valendo e podem ser recebidos. **Liberar fiado** exige telefone e documento (pede ali mesmo se faltar) e um limite. Cada lançamento vence em 30 dias (valor de exemplo, vira configuração); pagamentos abatem **o mais antigo primeiro**. Situações em palavra: Em dia, Em aberto, Vencido há N dias, Acima do limite (limite reduzido depois). **Receber** (`conta_receber:baixar`) pede unidade, valor (não passa do saldo) e forma, e diz que entra no caixa aberto. Alterar limite pede motivo.

Pontos abertos: (1) Fiado na comanda ainda usa a lista fixa do protótipo de Comandas, não este cadastro. (2) Prazo do fiado e limite padrão por perfil (`02d` §5.3). (3) Importação de clientes por planilha (F17). (4) Fidelidade, reservas e campanhas ficam fora (F16 "preparado, não construído"). (5) Cliente na comanda e histórico por comanda real: hoje o histórico é gerado dos totais de exemplo.

## U15. Compras e fornecedores (F3.2, F3.3, F3.14 a F3.17, F18.8, protótipo web)

Quatro abas: **Compras**, **Fornecedores**, **A pedir** (`compra:criar` ou `pedido_compra:criar`) e **Preços** (`compra:ver_historico_preco`, com contador de aumentos). **Fornecedor é da organização; compra é da unidade** (seletor do menu, U10). O selo do menu lateral conta compras que chegam hoje ou estão atrasadas.

**Compras** (`fornecedor:ver`): resumo (chegam hoje, atrasadas, a pagar em 7 dias, compras em 30 dias; valores só com `compra:ver_precos`), busca, chips (Aguardando entrega, Atrasadas, A pagar, Recebidas, Canceladas) e lista paginada com fornecedor, tipo, situação em palavra (**Aguardando entrega**, **Atrasada**, **Recebida em parte**, **Recebida**, **Cancelada**, **Estornada**), entrega, valor e situação da conta a pagar. Pendentes primeiro, por data de entrega.
- **Tipos (F3.15):** *À vista* (já entra recebida e paga), *Agendada* (data e hora previstas; no recebimento vira conta a pagar se o fornecedor tem prazo) e *A prazo* (entra agora, vencimento escolhido). **Nova compra** escolhe fornecedor, tipo e itens (qtd em kg, L ou un, preço sugerido pelo último pago, aviso **▲ N%** quando passa do limite).
- **Receber** (`compra:receber`): informa quanto chegou e o preço pago de cada item. Gera `entrada_compra` no Estoque, **recalcula o custo médio ponderado** e, se faltou algo, a compra fica **Recebida em parte** (dá para receber o restante ou **encerrar sem o restante**). A divergência vai para o histórico da compra e para um aviso na tela.
- **Janela da compra:** *Itens* (pedido, recebido, preço, **contra o último pago**), *Pagamento e nota* (condição, vencimento, conta a pagar, NF-e) e *Histórico*. Ações por estado: Receber, Reagendar (motivo), Enviar pedido, Fornecedor confirmou, Cancelar e **Estornar** (`compra:cancelar`, motivo obrigatório).
- **Estorno (F3.14):** tira do estoque o que a compra pôs, acerta o custo médio e cancela a conta. **Recusa** se parte já foi consumida (saldo menor que o recebido), a menos que a unidade permita estoque negativo.
- **Pedido ao fornecedor (F18.8):** estados *rascunho, enviado, confirmado*. **Enviar** (`pedido_compra:enviar`) gera o PDF e manda por e-mail; sem e-mail cadastrado, avisa e oferece baixar o PDF. WhatsApp fica para depois.

**Importar NF-e** (`compra:importar_nfe`, F3.16): escolher o XML (só XML, até 2 MB, checado pelo conteúdo, sem entidades externas; o protótipo sempre abre uma nota de exemplo). **Conferência antes de confirmar:** fornecedor achado pelo CNPJ; vínculo com a **compra agendada** do mesmo fornecedor; cada item da nota mostra como casou (EAN, código ou descrição), o insumo escolhido (editável, ou "Ignorar este item") e as divergências em palavra (quantidade ou preço diferentes do pedido, preço acima do último, sem correspondência). Nada entra no estoque até confirmar; a **chave da nota** impede importar duas vezes.

**Fornecedores:** lista com contato, condição (À vista, boleto em N dias), rotina e próxima entrega. **Ficha** (janela, abas): *Resumo* (compras abertas, a pagar, contato, rotina), *Compras*, *Insumos e preços* (o que fornece, tendência, selo **Melhor preço**) e *Cadastro* (nome, CNPJ único, categoria, condição, contato, e-mail dos pedidos, **rotina de entrega** com dias da semana, horário limite e antecedência, ativo). Sem `fornecedor:gerenciar` tudo é só leitura.

**A pedir (F3.17 + sugestão de compra):** exige unidade. Agrupa por fornecedor preferido os insumos **abaixo do mínimo** ou zerados, com situação, saldo, **quanto dura**, quantidade sugerida (2 vezes o mínimo ou 5 dias de consumo, o que for maior) e último preço. Cada grupo mostra o **prazo para pedir** segundo a rotina ("Pedir até dom, 25/10 16h") e **Criar pedido** abre a compra agendada já preenchida. Itens já cobertos por uma compra aberta aparecem como "Já pedido na compra #N".

**Preços:** *Subiu além do limite* (último preço contra a média das compras anteriores do mesmo fornecedor; limite de 5, 10, 15 ou 20%, padrão 10%) e *Ranking de preço* por insumo, com mini gráfico, número de compras, menor já pago e selo Melhor preço.

Pontos abertos: (1) Conta a pagar é só exibida; pagar é no Financeiro (ainda sem tela). (2) Editar itens de compra já criada (hoje só reagendar). (3) Conversão por embalagem (caixa, barril) e lote e validade (F3.13) ficaram de fora: a compra é em kg, L e un. (4) Orçamento por categoria (F18.8) e o aviso "Hora de pedir" nas notificações não foram ligados. (5) Compra por unidade em "Todas as unidades" só lista; criar e receber pedem escolher uma. (6) Leitura real do XML e envio real de e-mail não existem no protótipo.

## U16. Control Tower (F12, protótipo web)

Arquivo próprio, `prototipos/control-tower.html` (outro sistema, outro login; mesma identidade, tokens e componentes do `index.html`). Só web: administradores trabalham no computador. Dados de exemplo em `localStorage` (`flow.ct`); "Restaurar" no rodapé do menu. Parâmetros de endereço no cabeçalho do arquivo (`&profile=S|U|F|O|L`).

- **Entrada:** login separado (D12), com o bloqueio por IP visível (`&ip=bloqueado`: "endereço fora da lista permitida", A10.1) e a regra de 5 tentativas. No protótipo entra-se direto como um dos 5 perfis (`02d` §4).
- **Marcas de contexto** na barra de cima: pílula **Produção** e **IP permitido · endereço**, para nunca confundir com o app do cliente. Menu mais sóbrio (marca em preto), filtrado pelas permissões da plataforma.
- **Menu:** *Plataforma* (Visão geral, Organizações, Cobrança, Planos) · *Produto* (Funcionalidades, Comunicados) · *Operação e segurança* (Operação, Auditoria, Administradores, Configurações). Perfil sem a permissão não vê o item; entrar na tela por endereço mostra "Seu perfil não vê esta tela" com a permissão que falta.
- **Visão geral (F12.1, F12.14):** quatro números (ativas, em teste com "acaba em até 3 dias", receita mensal com atraso, comandas hoje), gráfico de 14 dias, lista **Precisa de atenção** (crítico, atenção, info, com a palavra além da cor) e tabela de uso por organização, paginada.
- **Organizações (F12.2):** lista paginada com chips por situação (Em teste, Ativas, **Em atraso**, Suspensas, Bloqueadas, Encerradas) e **ficha em página própria** com trilha (`Control Tower › Organizações › Bar do Zé`), não janela: Resumo, **Assinatura**, Unidades, Usuários, Funcionalidades, Histórico.
- **Assinatura (F12.11):** faixa com os 5 estados, o atual com contorno forte e os alcançáveis com contorno cheio (transição inválida é recusada, inclusive no código). Ações aparecem conforme **estado de origem e permissão**: prorrogar teste, converter, registrar pagamento, cortesia, exceção manual, agendar (e cancelar o agendamento), suspender (leitura ou bloqueio total), bloquear, reativar, cancelar (agenda o encerramento ao fim do período pago), desfazer, encerrar agora (digitar o nome). **Toda ação pede motivo**, grava histórico com autor e horário, entra na auditoria e avisa o dono. Ações com limite (`[L]`) mostram o limite do perfil e recusam acima dele. **Bloqueada só o super admin libera** (o botão nem aparece para os outros).
- **Impersonação (F12.6, D15):** botão na ficha. Janela pede usuário, modo, duração e motivo; modo e duração respeitam a **política da pessoa** (Suporte: só leitura, até 30 min, motivo obrigatório, organizações atribuídas; botão desabilitado com a razão quando a organização está fora). Durante a sessão há **faixa fixa em rosa-carmim** em todas as telas (quem, onde, modo, tempo restante, Encerrar) e uma tela com as garantias (organização informada, ato atribuído ao admin real, sem acesso a segredos). Encerra sozinha no limite de tempo; link "Abrir o app do cliente" leva ao `index.html` com o perfil.
- **Cobrança, Planos, Funcionalidades, Comunicados:** cobrança com MRR, atrasos (com tolerância restante) e vencimentos; planos com preço e limites (recusa limite abaixo do uso atual); funcionalidades como matriz **funcionalidade × plano** com chave, **exceção por organização** e confirmação com motivo mostrando quantas organizações mudam; comunicados com alvo (todas ou escolhidas), expiração e **acompanhamento de leitura**.
- **Operação:** filas, rotinas e integrações com situação em palavra, e a zona de **Emergência** (só super admin): revogar sessões de administradores, suspender uma organização agora, rotacionar segredos; cada uma exige digitar CONFIRMAR e motivo.
- **Auditoria (F12.7, S14):** tabela paginada (25 por página) com busca, filtro por pessoa e tipo; cada linha mostra o **hash encadeado**; "Verificar integridade" refaz a cadeia. Só no protótipo, "Simular adulteração" altera um registro para a verificação falhar e abrir incidente.
- **Administradores (F12.8):** pessoas (perfil, situação, último login e IP, política de impersonação resumida) e **matriz de permissões por perfil** com marcas A (auditada) e L (limite). Ficha com perfil, escopo por organização, política de impersonação, desativar; **pede a senha de novo** (reautenticação, S13). Super admin é conta protegida: perfil e escopo travados.
- **Configurações (F12.15):** abas Assinaturas (teste padrão, tolerância, fim do teste, janela de leitura, retenção), Plataforma (contato, limites padrão, texto da tela de suspensa), **Acesso e IP** (lista permitida; não deixa remover o próprio IP nem esvaziar a lista) e Histórico de versões. Barra fixa "N alterações não salvas" e motivo ao salvar, como em U10.

Achados ao prototipar: (1) `02d` §4 diz **34** permissões da plataforma, mas a tabela tem **35**. (2) Os limites `[L]` (prorrogar teste 30 e 15 dias, cortesia 60, exceção 15, ajuste de cobrança 20%) e os preços e limites dos planos são **valores de exemplo**, ainda decisão do dono. (3) Quem **reativa** uma organização bloqueada: a tabela dá `assinatura:reativar` a Financeiro e Operações, mas F12 diz que só o super admin libera; o protótipo aplica a regra de F12 por cima da permissão. (4) O super admin é único; "Novo administrador" não oferece esse perfil. (5) Falta decidir se **Financeiro** pode ver a lista de usuários da organização (hoje só `organizacao:ver`).

Pontos abertos: (1) notificações da plataforma (bolinha da Control Tower, `02f` área Plataforma) ainda sem tela. (2) Edição dos dados da organização (`organizacao:editar`) e **troca de plano** fora da conversão do teste. (3) Reautenticação e 2FA aparecem só como texto: o campo de senha do protótipo aceita qualquer valor. (4) Painel de restauração de backup e detalhe de cada job. (5) Versão celular: fora de escopo, a tela só reduz o menu.

## U17. Financeiro (F8, protótipo web)

`#screen=financeiro&ntab=visao|pagar|receber|contas|dre|cat&conta=p3`. Dados em `localStorage` (`flow.financeiro`). **Centro de custo é a unidade** do seletor (U10); contas financeiras podem valer para a organização inteira. Seis abas, filtradas pela permissão (`02d` §2): **Visão geral**, **Contas a pagar**, **A receber** (`conta_receber:ver`), **Contas financeiras** (`conta_financeira:gerenciar` ou `financeiro:ver_dre`), **DRE** (`financeiro:ver_dre`), **Categorias** (`financeiro:categorias_gerenciar`). O selo do menu conta contas vencidas ou que vencem hoje.

- **Visão geral:** faixa de alerta (contas vencidas e fiado vencido, F8.9), quatro números (saldo, a pagar e a receber em 7 dias, fiado), **fluxo de caixa** de 8 semanas (4 realizadas, sólidas; 4 projetadas por vencimento, tracejadas; entradas e saídas com legenda e tabela de saldo projetado) e lista dos próximos 7 dias com sinal + e −.
- **Contas a pagar (F8.1, F8.2):** lista paginada (U9) com busca, categoria e chips (Em aberto, Vencidas, Vencem em 7 dias, Pagas, Canceladas, Todas) com contagem. Vencida em carmim com a palavra "Vencida há N dias". **Compra a prazo recebida gera a conta sozinha** (aparece como "gerada pela compra"); pagar aqui dá baixa na compra, e alterar ou cancelar só pela compra. **Nova conta:** descrição, favorecido, categoria, valor, vencimento, **parcelas** (1 a 12, valor dividido), **repetir todo mês**, centro de custo e anexo (só o nome fica no histórico). **Pagar:** valor (parcial permitido), data e **conta de saída**; recusa valor acima do **limite do perfil** (Dono sem limite, Gerente R$ 3.000, Financeiro R$ 5.000, exemplo) e saldo insuficiente. Editar só sem pagamento e pede motivo se mudar o valor; **cancelar** pede motivo e só sem pagamento. Cada conta tem janela com pagamentos e histórico.
- **A receber (F8.3):** repasses de operadora com **taxa já descontada**, previsão, situação (Previsto, Cai hoje, Atrasado, Recebido) e **Dar baixa** em uma conta financeira; se veio menos que o previsto, a diferença fica anotada. **Fiado** só acompanha: o cadastro e o recebimento continuam em Clientes (fonte única), com atalho.
- **Contas financeiras (F8.4):** caixa físico, conta bancária e carteira de operadora. **Saldo = soma dos lançamentos**; o extrato é imutável (erro se corrige com novo lançamento). **Transferir** (com limite, Financeiro R$ 10.000) gera dois lançamentos ligados; **Nova conta** com saldo inicial.
- **DRE (F8.8):** Mês atual (do dia 1 até hoje) comparado ao **mesmo trecho do mês anterior**, ou Mês anterior fechado. Receita bruta, descontos, taxas, receita líquida, CMV, perdas, lucro bruto, despesas por grupo e resultado, com % da receita líquida e variação em **palavra e cor** ("melhor" ou "pior": despesa que sobe é pior). Compras de insumos ficam só no CMV, nunca duplicadas nas despesas.
- **Categorias (F8.5):** plano de contas simples (grupo no DRE, nº de contas, ativar, desativar, renomear).
- **Exportar** (`financeiro:exportar`) pede motivo e registra na auditoria.

Perfis no protótipo: **Financeiro** vê tudo; **Dono** também; **Gerente** vê Visão, Pagar (cria, edita, paga até o limite, não cancela) e A receber, **sem** saldos, DRE e categorias; **Somente leitura** vê Visão, Pagar, A receber, Contas e DRE, sem nenhum botão de ação; Caixa, Garçom, Estoquista e Cozinha não veem o menu.

Achados ao prototipar: (1) a tabela do `02d` deixa **saldos de contas** sem permissão própria; o protótipo os atrela a `conta_financeira:gerenciar` ou `financeiro:ver_dre`, o que esconde saldo do Gerente. Decidir. (2) Limites de pagar e transferir por perfil são de exemplo (`02d` §5.3). (3) `conta_receber:ver` e `conta_pagar:*` entraram no catálogo de permissões do protótipo; os 210 itens de Perfis já os continham. (4) Pagar uma compra pelo Financeiro foi mantido total, sem parcial, porque a compra tem um só vencimento.

Pontos abertos: (1) **Conciliação** (marcar lançamento como conferido com o extrato) e importação de extrato. (2) Estorno de pagamento e de baixa. (3) Anexos reais e leitura de boleto. (4) Centros de custo além da unidade. (5) Projeção do fluxo com sazonalidade e vendas previstas, hoje só vencimentos. (6) Calendário (camada Pagamentos) ainda usa dados fixos, não estas contas. (7) Versão celular: quarta aba "Mais" lista Financeiro como "Em breve".

## U18. Caixa (F5, protótipo web)

`#screen=caixa&jtab=agora|turnos|aprov|dia&turno=t5`. Dados em `localStorage` (`flow.caixa`); regras da unidade (caixas, conferência cega, tolerância, sangria sem aprovação) vêm de Configurações › Caixa. O selo do menu conta o que espera a pessoa: diferenças a aprovar (quem aprova), recontagem pedida e caixa próprio aberto há mais de 14 h.

**Abas** (filtradas por permissão): *Agora*, *Turnos*, *Aprovações* (`caixa:aprovar_diferenca` e `ver_todos`, com contador) e *Dia operacional* (`caixa:ver_todos`). Sem `ver_todos` a pessoa só enxerga os próprios turnos.

- **Agora:** com caixa aberto, quatro números (dinheiro na gaveta, vendas, sangrias, tempo aberto), recebimentos por forma de pagamento, últimos movimentos e as **comandas esperando o caixa** (com taxa de serviço da unidade). Ações: **Suprimento**, **Sangria**, **Fechar caixa**. Sem caixa aberto, cartão com **Abrir caixa** (escolhe o caixa livre e informa o **fundo de troco**, que não é venda). Quem tem `ver_todos` também vê a tabela de caixas da unidade (livre, aberto por quem, há quanto tempo). Faixas: recontagem pedida, caixa aberto há muito tempo, diferenças a aprovar. Em "Todas as unidades" só lista; abrir e movimentar pedem escolher uma.
- **Conferência cega (`fechar_cego`, só o Caixa):** o operador **não vê** dinheiro na gaveta, vendas nem o valor em dinheiro de cada recebimento (aparece "•••"); vê só quantos recebimentos fez. No fechamento, o campo de dinheiro vem sem "esperado". O resultado aparece depois de fechar. Gerente e dono, sem essa permissão, veem o esperado.
- **Suprimento e sangria:** valor, motivo obrigatório e, na sangria, destino (cofre, banco, fornecedor, outro). Recusam acima do **limite do perfil** (Caixa R$ 300, Gerente R$ 2.000, Dono sem limite; valores de exemplo) e sangria maior que o dinheiro esperado. Sangria acima de "Sangria sem aprovação" (Configurações) só quem aprova diferença registra.
- **Fechar caixa:** contagem do dinheiro (obrigatória, pode ser zero) e total conferido de Pix, crédito, débito e vale-refeição (opcional; em branco não conferido). Esperado do dinheiro = fundo + recebimentos + suprimentos − sangrias − estornos. **Dentro da tolerância** fecha sozinho e registra a diferença; **acima**, fica **Aguardando aprovação**. A diferença aparece em palavra e cor: "Confere", "Falta R$ x" (carmim), "Sobra R$ x" (âmbar).
- **Aprovações:** cada fechamento pendente mostra esperado × contado × diferença por forma. **Aprovar** (motivo; gerente até R$ 50, dono sem limite) ou **Pedir recontagem** (motivo; o operador refaz a contagem). **Quem fechou não aprova a própria diferença**, exceto o dono (autoaprovação auditada); o botão fica desabilitado com a razão em texto. Abaixo, as aprovadas recentemente.
- **Turnos (F5.6):** busca por operador ou caixa, chips (Todos, Abertos, Aguardando aprovação, Com diferença, Fechados) com contagem, lista paginada (U9) e **janela do turno** com resumo, conferência, movimentos e histórico. **Reabrir** (`caixa:reabrir`, motivo, descarta a contagem anterior) só enquanto o dia operacional não estiver fechado.
- **Dia operacional (F5.5):** escolhe Hoje, Ontem ou dois dias atrás; mostra vendas, caixas, diferença total e vendas por forma. **Fechar o dia** só habilita quando todos os caixas fecharam e nenhuma diferença espera aprovação; a faixa lista o que falta. Depois de fechado, os turnos do dia não reabrem.

Dados de exemplo: Centro (1 caixa) com a Paula aberta; Praia (2 caixas) com diferenças a aprovar; uma acima do limite do gerente, que só o dono aprova.

Pontos abertos: (1) A **janela operacional** só aparece como texto; abrir fora dela e `caixa:registrar_fora_do_horario` não são aplicados. (2) Recebimento do protótipo é simulado no caixa; as comandas não gravam o pagamento no turno. (3) Sangria com destino banco ainda não gera lançamento no Financeiro. (4) Fechamento automático ao fim da janela e lembrete. (5) Fechar o dia usa `caixa:aprovar_diferenca`, porque o catálogo não tem permissão própria. (6) Contagem por cédulas e moedas. (7) Versão celular: "Em breve", como em U7.

## U19. Equipe e comissões (F9, protótipo web)

`#screen=equipe&wtab=colab|com|regra&colab=c2`. Dados em `localStorage` (`flow.equipe`). Três abas, filtradas por permissão: **Colaboradores** (`equipe:ver`), **Comissões** (`comissao:ver_todas`; só `ver_proprias` mostra apenas as do próprio nome) e **Regra de comissão** (`comissao:configurar`). O contador da aba Comissões é o número de lançamentos a pagar.

- **Colaboradores:** nome, função (garçom, caixa, cozinha e bar, estoque), unidade, **acesso** (usuário e PIN, ou "Sem acesso ao sistema": colaborador existe sem usuário), comissão (regra padrão, percentual próprio ou "Não comissiona") e saldo a pagar. A **ficha** (janela) mostra últimos 30 dias (vendas, ticket médio, comissão, a pagar), lançamentos recentes e histórico do cadastro; **Editar** e **Desativar** com motivo (`colaborador_gerenciar`). Só garçom comissiona nesta fase.
- **Regra (F9):** percentual padrão, valor-base (venda sem ou com taxa de serviço) e se cortesias comissionam (padrão não). Salvar pede motivo, vale para vendas novas e registra a mudança; cada lançamento guarda a regra usada. O percentual próprio vence a regra padrão.
- **Comissões:** lançamentos por comanda (data, base, percentual, valor, situação em palavra: *A pagar*, *Paga*, *Revertida*, *Excluída*), busca, filtro por colaborador, chips com contagem e paginação (U9). **Saldo por colaborador** com **Pagar**: escolhe a conta financeira, recusa saldo insuficiente e gera a **saída no extrato do Financeiro** (U17). **Ajustar** pede novo valor e motivo e respeita o **limite do perfil** (Gerente R$ 100 de diferença, Dono sem limite; valores de exemplo); **Excluir** pede motivo e deixa o lançamento no histórico. Estorno reverte sozinho (exemplo no Carlos).

Pontos abertos: (1) Fechamento de comissão por período (opcional em F9). (2) Os lançamentos do protótipo não nascem do fechamento das comandas. (3) Escala, turnos e metas ficam fora. (4) Criar o usuário de acesso a partir do colaborador (hoje é em Usuários). (5) Limite de ajuste por perfil (`02d` §5.3).

## U20. Relatórios (F10, protótipo web)

`#screen=relatorios&rtab=ini|vendas|prod|caixa|equipe|custos&per=hoje|7|30|mes`. Seis abas, cada uma só para quem tem a permissão: **Início** (catálogo), **Vendas** e **Produtos** (`relatorio:ver_vendas`), **Caixa** (com `caixa:ver`), **Equipe** (com `comissao:ver_todas`) e **Custos e CMV** (`relatorio:ver_custos`). Período em Hoje, 7 dias, 30 dias e Mês atual; unidade pelo seletor do menu (U10), com "Todas" só para `relatorio:ver_todas_unidades`. **Exportar** (`relatorio:exportar`) pede motivo e entra na auditoria. Toda comparação é **contra o período anterior de mesmo tamanho**, em palavra e cor ("melhor", "pior").

- **Início:** os relatórios do sistema atual, todos, como cartões que abrem a aba certa (vendas do dia, por produto, mais vendidos, pico por horário, por garçom, fechamento de caixa, CMV por produto, perdas e cortesias, engenharia de cardápio, CMV teórico × real, vendas e comissões da equipe) ou levam à tela dona (DRE no Financeiro, movimentações no Estoque, histórico em Comandas). Sem permissão, o cartão diz que o perfil não vê.
- **Vendas:** faturamento, comandas, ticket médio, descontos e cortesias; faturamento por dia (ou por hora, em "Hoje"), pico por horário, dia da semana, formas de pagamento, garçom e **unidade contra unidade**.
- **Produtos:** ranking com **curva ABC** (A até 80%, B até 95%); com ver custos, margem, CMV% e a **engenharia de cardápio** em quatro grupos (Estrela, Vaca leiteira, Abacaxi, Cão) com a ação sugerida.
- **Caixa:** turnos fechados, vendas, sangrias e diferença por operador, comparando cada pessoa com a média dos pares.
- **Equipe:** vendas, ticket e comissão por colaborador, com o saldo a pagar.
- **Custos:** CMV por produto, lucro bruto estimado, perdas, cortesias e **CMV teórico × real** por insumo, com "investigar" quando passa de 5%.

Pontos abertos: (1) Vendas e custos são **dados gerados** (60 dias), não vêm das comandas. (2) Relatórios de estoque e financeiro só têm atalho para as telas. (3) Agendamento e exportação real (CSV, XLSX, PDF). (4) Filtros por operador, forma de pagamento e produto. (5) Posição de estoque em data passada. (6) Curva ABC e engenharia por unidade em "Todas".

## U8. Pendências deste documento

- Escolha final de como a comanda abre (painel, janela, tela cheia) depois de testar.
- Se a organização pode renomear ou recolorir as áreas das notificações (as cores dos tipos, âmbar para Ação e azul para Aviso, são fixas).
- Respostas do dono do restaurante: luz baixa ou normal, densidade, entrada de quantidade, confirmar antes de enviar.
- Telas ainda não prototipadas: documentos, auditoria. (Dashboard: U21; Equipe: U19; Relatórios: U20; Caixa: U18; Financeiro: U17; Control Tower: U16; Calendário: U6; Usuários e perfis: U7b; Unidades e configurações: U10; Cozinha e mesas: U11; Cardápio: U12; Estoque: U13; Clientes: U14; Compras e fornecedores: U15.)
- Catálogo de componentes e design system (começo do trabalho foi interrompido; ver prototipação).
- Acessibilidade: contraste foi calculado, mas falta teste com leitor de tela e em aparelho real.
- Fonte e paleta finais, depois de uso real da bolinha de configuração.
