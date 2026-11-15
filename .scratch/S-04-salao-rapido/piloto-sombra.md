# Piloto em modo sombra: roteiro da noite (F4-0072)

Status: RASCUNHO para o dono aprovar. Nada aqui foi ensaiado com garçons reais; o que foi provado por máquina está em "O que já foi provado" e o que depende de gente está marcado como pendência.

Modo sombra = os garçons e o caixa lançam no sistema novo em paralelo ao que já usam. O sistema antigo e o caderno/maquininha continuam sendo a verdade da noite. Nada do que for lançado aqui alimenta caixa, estoque, fiscal, financeiro ou relatório oficial.

## O que já foi provado (por máquina)
- A noite inteira por HTTP em duas organizações, por Garçom, Caixa e Gerente criados por perfil modelo, sem o dono: `DoisTenantsEndpointsTests.A_scripted_night_runs_in_each_organization_by_model_profiles_without_the_owner_and_nothing_crosses_to_the_other` (abrir turno, comandas com e sem mesa, adicional, repetir rodada, transferir item, juntar, desconto dentro e acima do limite, cortesia, as cinco formas de dividir, pagamento por parte e misto, suprimento, sangria, fechar com diferença aprovada pelo gerente).
- Metas de toque e tempo medidas no navegador contra a API real: ver `metas.txt` (só o que foi de fato medido consta lá).
- NÃO provado: uso por pessoas reais, aparelho real (só emulação de 390x844 e 360x740), sinal ruim de verdade, impressão do recibo, noite longa (o que acontece às 3 da manhã).

## Antes da noite (pré-requisitos)
1. Ambiente: usar o STAGING (`docs/runbooks/deploy.md`), nunca produção. Staging e produção não compartilham banco; o staging só tem dado descartável. Confirme `GET /health/ready` com o `commit` esperado.
2. Uma organização exclusiva "Piloto sombra" (não reaproveitar organização de teste de outra rodada). O dono entra com a conta de fundador.
3. PENDÊNCIA DO DONO: hoje NÃO existe rota para o dono criar a conta de um garçom (só o seed e o convite por e-mail; o convite ainda não aplica perfil, ver `.scratch/governanca/pendencias.md`). Até decidir o fluxo, as contas operacionais da noite são criadas por quem tem a credencial do banco (rodando o seed contra o staging) e a senha temporária é entregue em mãos. Decida: convite com perfil, ou criar conta com senha temporária.
4. O dono, pela tela de usuários e perfis: criar os três perfis modelo (Garçom, Caixa, Gerente) e atribuir cada pessoa. Conferir os limites do Gerente (desconto até 20% e R$ 150,00; cortesia e brinde até R$ 80,00; aprova diferença até R$ 200,00) e do Caixa (suprimento até R$ 500,00, sangria até R$ 1.000,00, não aprova diferença). Ajuste se o restaurante quiser outros números.
5. Cardápio: importar a planilha real pela tela de importação (prévia primeiro; corrigir os erros que o relatório mostrar; só então aplicar). Conferir 5 preços ao acaso contra o cardápio de papel. Cadastrar mesas e áreas.
6. Opções do caixa (Configurações): decidir `caixa.conferencia-cega` (o caixa conta sem ver o esperado) e deixar `caixa.permitir-autoaprovacao` desligada.
7. Aparelhos: cada pessoa entra uma vez ainda com Wi-Fi, adiciona o app à tela inicial e abre `/salao`. Teste com o avião ligado: a fila offline deve guardar o lançamento e enviar ao voltar.
8. Combinar a regra de ouro: se o sistema atrapalhar o atendimento, esqueça o sistema e atenda; anote depois.

## Quem faz o quê
| Papel | Faz no sistema | Não faz |
|---|---|---|
| Garçom (2 a 3) | abre comanda (com ou sem mesa), lança com adicionais, repete rodada, desfaz rodada, transfere item/mesa, renomeia | desconto, cortesia, juntar, receber |
| Caixa (1) | abre o turno com o fundo, recebe (um ou vários métodos, por parte), fecha a comanda, suprimento, sangria, fecha o turno | aprovar a própria diferença |
| Gerente (1) | desconto e cortesia dentro do limite, juntar comandas, cancelar, aprovar diferença do caixa | |
| Dono | prepara tudo antes, acompanha, decide o que fazer com cada atrito | operar durante a noite |
| Observador (1, não é garçom) | cronometra, anota atritos na ficha, não ajuda sem ser pedido | |

## Roteiro da noite
Antes de abrir: caixa abre o turno com o fundo real (o mesmo que está na gaveta).
1. Cada pedido real de mesa que o garçom lançaria no papel, ele também lança no sistema. O papel continua valendo.
2. Cenas obrigatórias (o observador marca as que acontecerem naturalmente e provoca as que faltarem, com valor simbólico):
   - uma comanda sem mesa (balcão) e uma com mesa;
   - um item com adicional, uma rodada repetida e uma rodada desfeita dentro da janela;
   - uma troca de mesa e uma transferência de item entre comandas;
   - duas comandas juntadas pelo gerente;
   - um desconto dentro do limite e um pedido ACIMA do limite (deve ser negado sem explicação na tela; anotar se a tela deixou claro o que fazer);
   - uma cortesia pelo gerente;
   - conta dividida (iguais e por assento, no mínimo) e recebida por partes;
   - um pagamento misto (por exemplo cartão com gorjeta mais dinheiro com troco);
   - pré-conta pedida e recibo segunda via;
   - um suprimento e uma sangria;
   - perda de sinal por 2 minutos durante um lançamento.
3. Ao fechar a noite: caixa conta a gaveta e fecha o turno. Se a diferença passar da tolerância (R$ 5,00), o gerente aprova com motivo.
4. Conferência (fora do sistema, no dia seguinte): comparar o total vendido do sistema novo com o do sistema antigo/papel, comanda a comanda. Toda divergência vira linha na ficha.

## Ficha de atrito (uma linha por ocorrência)
Colunas: hora | quem | tela/ação | o que aconteceu | o que a pessoa esperava | gravidade (parou o atendimento / atrasou / incomodou) | toques ou segundos gastos (se medido) | foto ou print | decisão do dono (corrigir / aceitar / adiar).
Critério sugerido para o dono decidir passar da sombra para uso real: nenhuma ocorrência "parou o atendimento" sem correção, zero divergência de valor entre sistema e papel, e as metas de toque cumpridas em aparelho real.

## Como zerar os dados da sombra
O sombra roda na organização exclusiva do staging; nada lê dela fora do próprio sistema.
- Caminho recomendado (sem apagar linha): nova rodada = nova organização "Piloto sombra 2". A anterior fica parada e ninguém entra nela. Não há rota nem rotina de apagar organização, e várias tabelas são só de acréscimo (auditoria encadeada, pagamentos) de propósito.
- Para limpar tudo de uma vez: recriar o banco do STAGING (Railway, ambiente `staging`, apagar e recriar o serviço de banco) e subir de novo; o migrador do pré-deploy reaplica todas as migrações no próximo deploy (`docs/runbooks/deploy.md`). Isso apaga também qualquer outro teste que esteja no staging. NUNCA em produção.
- Não ensaiado: nenhum dos dois caminhos foi executado nesta entrega (o banco do staging não foi tocado). Ensaiar uma vez antes da noite.

## Plano de volta (rollback)
- O sistema antigo e o papel nunca foram desligados: voltar é parar de abrir o app. Não há migração de dados do antigo para o novo nem o contrário.
- Se o sistema cair no meio da noite: o app guarda os lançamentos na fila do aparelho; o garçom segue no papel. Não reinicie nem limpe o navegador antes de o dono olhar a fila.
- Se um deploy novo atrapalhar durante a noite: pedir ao dono para congelar `dev-v2` (sem push) até o fim da noite; se já subiu, voltar o deploy anterior no Railway (`docs/runbooks/deploy.md`).
- Nada do sombra entra em contabilidade, fiscal, estoque ou comissão: o resultado da noite é só a ficha de atrito e a conferência de valores.
- Produção não tem backup de banco (decisão do dono, F4-0098); por isso o piloto não roda em produção.

## Pendências para o dono aprovar
1. Fluxo de criação das contas dos garçons (item 3 dos pré-requisitos).
2. Os limites dos perfis modelo (Gerente e Caixa) e a regra "caixa não aprova a própria diferença".
3. Data da noite, quem são o observador e o gerente.
4. Confirmar a taxa de serviço de 10% sobre o consumo já descontado (pergunta Q4 do spec, ainda marcada como proposta).
5. Ensaiar o zerar-dados no staging antes da noite.
