# Novo sistema — Segurança

Status: rascunho 1. Independe de stack: define **o que precisa ser verdade**; `04-arquitetura` escolhe como. Riscos aceitos estão em `01-visao`.

## S1. Princípios

1. **Negar por padrão.** Sem permissão explícita, nenhuma ação.
2. **O servidor decide.** Nada de segurança depende do front-end.
3. **Isolamento de organização em camadas.** Uma falha em uma camada não vaza dados.
4. **Menor privilégio.** Pessoas, serviços e administradores recebem só o necessário.
5. **Tudo sensível é auditado**, de forma imutável.
6. **Seguro por construção.** O jeito mais fácil de escrever uma rota nova é o jeito seguro; o inseguro falha em teste ou em compilação.
7. **Defesa em profundidade.** Nenhum controle único é a única barreira.
8. **Falhar fechado.** Erro de autorização ou de configuração nega acesso, não libera.

## S2. O que protegemos (ativos)

| Ativo | Impacto se comprometido |
|---|---|
| Dados de uma organização (vendas, custos, margens, estoque, financeiro) | Concorrência, vazamento comercial, ação judicial |
| Dados pessoais (funcionários, clientes de fiado, fiscal) | LGPD, multa, dano a terceiros |
| Dinheiro: pagamentos, caixa, comissões, estornos | Fraude interna, desvio |
| Credenciais e sessões | Tomada de conta |
| Certificado e dados fiscais | Emissão fraudulenta em nome do cliente |
| Control Tower e super admin | **Comprometimento de todas as organizações** |
| Integridade dos registros e auditoria | Perda de prova, fraude invisível |
| Disponibilidade da operação | Casa parada em horário de pico |

## S3. Quem ameaça (atores)

| Ator | Exemplos |
|---|---|
| Atacante externo anônimo | Força bruta, credential stuffing, varredura, injeção, bots |
| Usuário malicioso de uma organização | Garçom que desvia, tenta ver outra unidade, escala permissão |
| Funcionário que saiu | Mantém acesso, sessão ou PIN |
| **Organização cliente contra outra** | Tentar ler ou alterar dados de outro tenant |
| Administrador da plataforma malicioso ou comprometido | Impersonação, exportação indevida |
| Fornecedor/dependência comprometida | Pacote malicioso, provedor de nuvem |
| Erro humano nosso | Configuração errada, segredo vazado, migração que apaga |

## S4. Modelo de ameaças (resumo STRIDE)

| Ameaça | Cenários principais | Controles principais |
|---|---|---|
| **Falsificação de identidade** | Roubo de senha, sessão ou PIN; phishing do dono | Hash Argon2id, bloqueio progressivo, sessões curtas com refresh rotativo, detecção de login novo, PIN preso a dispositivo registrado, (2FA reservado) |
| **Adulteração** | Alterar valor de venda, estoque, caixa; editar log | Registros financeiros e de estoque **imutáveis** (correção por estorno), auditoria append-only com encadeamento de integridade, validação no servidor |
| **Repúdio** | "Não fui eu que cancelei" | Ato sempre atribuído ao usuário real (e ao admin real na impersonação), motivo e antes/depois gravados |
| **Vazamento de informação** | Tenant lê outro; IDOR; exportação em massa; logs com dado pessoal | Isolamento por organização no banco e na aplicação, autorização por objeto, exportação como permissão auditada, sem dado pessoal em log |
| **Negação de serviço** | Flood de login, relatório pesado, tenant barulhento | Rate limit por IP/usuário/organização, filas justas, relatórios assíncronos, limites de plano |
| **Elevação de privilégio** | Garçom vira admin; autopromoção; deny ignorado | Autorização no servidor em todo caminho, ninguém altera as próprias permissões, deny vence allow, testes de matriz de permissões |

## S5. Isolamento entre organizações (prioridade máxima)

Toda tabela de negócio pertence a uma organização. A garantia é feita em **três camadas independentes**:

1. **Contexto de organização obrigatório.** Toda requisição autenticada resolve organização e unidade a partir do token e do vínculo do usuário, **nunca de parâmetro enviado pelo cliente**. O contexto é injetado pela infraestrutura e acompanha toda operação.
2. **Banco de dados impõe a regra.** Políticas de segurança por linha (RLS) ou mecanismo equivalente: o banco **recusa** ler ou gravar linha de outra organização, mesmo que a aplicação erre uma consulta. A conexão da aplicação **não** tem privilégio para ignorar a regra; migrações e administração usam credenciais separadas.
3. **Testes automáticos de isolamento.** Todo endpoint e toda consulta nova é testado com dois tenants: o teste falha se um enxergar ou alterar dado do outro. O CI bloqueia merge sem isso (RNF7).

Regras adicionais:
- Identificadores não sequenciais/não adivinháveis (UUID ou equivalente); mesmo assim a **autorização por objeto** é sempre verificada.
- Caches, filas, arquivos, buscas, notificações e logs também são segregados por organização (chaves e prefixos com organização).
- Trabalhos em segundo plano **carregam o contexto da organização** e o restabelecem antes de tocar dados.
- Relatórios agregados globais da plataforma usam caminho próprio e auditado (Control Tower), nunca "desligam" o isolamento.
- Escopo por **unidade** dentro da organização é aplicado na camada de autorização e coberto pelos mesmos testes.

## S6. Autenticação

- **Senhas:** Argon2id com parâmetros modernos; política mínima de comprimento (não de "símbolos obrigatórios"); checagem contra senhas vazadas; sem dicas, sem limite máximo irrisório; senhas nunca em log.
- **Contas operacionais** (usuário + senha/PIN, F11): credencial temporária única, mostrada uma vez, troca obrigatória; PIN com hash, restrito a dispositivo registrado e unidade, com bloqueio por tentativas e **sem acesso a funções administrativas, financeiras ou de exportação**.
- **Google:** OpenID Connect, só e-mail verificado; vínculo a conta existente exige confirmação autenticada; nenhuma criação de conta automática em organização existente.
- **Proteção de login:** bloqueio progressivo por conta **e** por IP, atraso uniforme, respostas que não revelam se o usuário existe, detecção de credential stuffing, CAPTCHA adaptativo sob ataque.
- **Recuperação de acesso:** link de uso único, expiração curta, invalida sessões antigas, notifica por todos os canais.
- **Convites:** token aleatório de uso único, com expiração e vínculo ao e-mail/perfil.
- **2FA:** fora do escopo, mas o modelo de identidade já tem o encaixe (fatores por usuário, política por organização). Mitigações compensatórias no S12.

## S7. Sessões e tokens

- **Token de acesso curto** (minutos) + **refresh rotativo** com detecção de reutilização (reuso derruba a cadeia inteira).
- Cookies `HttpOnly`, `Secure`, `SameSite`; proteção contra CSRF onde houver cookies; sem tokens em `localStorage`.
- Cada token carrega **audiência** (app dos clientes vs Control Tower): um **não vale no outro**.
- **Revogação imediata:** desativar usuário, trocar senha, mudar permissão ou remover de unidade invalida sessões afetadas (lista de revogação ou versão de sessão consultada no servidor).
- Sessões listadas e encerráveis pelo usuário e por quem administra; expiração por inatividade e duração máxima; sessão de PIN expira rápido.
- **"Sessão curta" explicado:** há dois relógios. (1) O **token de acesso** vive poucos minutos e é renovado em silêncio pelo refresh; o usuário **não percebe**, e serve para limitar o estrago de um token roubado. (2) A **sessão em si** tem um limite por inatividade e um limite absoluto, que são o que o usuário de fato sente (quando precisa digitar a senha de novo). Decisão: o relógio (1) é sempre curto e invisível; o relógio (2) é **configurável por perfil e organização, com padrão generoso** para não atrapalhar a operação (sugestão: salão, até o fim do turno; dono e gerente, dias). Sessão mais restrita para administradores fica disponível como política, desligada por padrão.
- Vínculo opcional a dispositivo; alerta e registro de login novo.

## S8. Autorização (modelo de permissões)

Complementa F11. Requisitos de segurança do motor:

- **Ponto único de decisão** (policy engine) chamado por toda operação; nenhuma verificação de permissão espalhada em regra de negócio.
- Entrada da decisão: **quem** (usuário, perfil, overrides), **o quê** (`recurso:ação`), **onde** (organização, unidade, objeto), **contexto** (valor, %, horário, dispositivo, tipo de sessão).
- Avaliação: **deny explícito > allow > negado por padrão**. Condições e limites (D10) avaliados no servidor, **sobre o valor real da operação**, não sobre o que o cliente diz.
- **Consistência com a operação:** a decisão e a ação ocorrem na mesma transação quando houver limite acumulado (ex.: "cortesia até R$ X por dia") para evitar corrida.
- **Anti-autopromoção:** ninguém altera as próprias permissões; só concede o que ele mesmo tem (sem escalada transitiva); sempre há ao menos um dono; dono não é rebaixado por outro.
- **Catálogo de permissões versionado** e gerado do código; permissão desconhecida nega.
- **Testes de matriz**: para cada perfil de fábrica, tabela esperada de permitido/negado verificada no CI; mudança de permissão sem atualizar a matriz falha.
- **Simulador** "o que este usuário pode fazer?" para administradores (F11) usa o mesmo motor, nunca uma cópia da lógica.
- Toda decisão **negada** em recurso sensível é registrada; padrões anormais geram alerta.

## S9. Proteção de dados

- **Em trânsito:** TLS 1.2+ em tudo (cliente, entre serviços, banco); HSTS.
- **Em repouso:** criptografia de disco/banco e de backups; chaves gerenciadas por serviço de chaves, com rotação.
- **Por campo (extra):** documentos pessoais, certificado digital fiscal, credenciais de integração (TEF, provedores), PIN/segredos de convite. Chaves separadas e, para o certificado fiscal, **por organização**.
- **Segredos:** em cofre de segredos, nunca no repositório; rotacionáveis; acesso mínimo por serviço; varredura de segredos no CI e no histórico.
- **Mascaramento:** valores e dados pessoais mascarados por padrão em telas de baixo privilégio e removidos de logs, e-mails e URLs.
- **Backups:** criptografados, em outra região, com teste de restauração; acesso restrito e auditado.
- **Ambientes não produtivos** jamais recebem dados reais.

## S10. Integridade financeira e de estoque

O ponto onde fraude interna acontece. Controles:
- **Imutabilidade:** pagamento, movimento de estoque, lançamento financeiro e movimento de caixa **não são editados nem apagados**; correção é um novo registro de estorno com motivo.
- Preço, custo e ficha **congelados** no lançamento (F1); mudar cardápio hoje não reescreve ontem.
- **Valores em inteiros (centavos)**; arredondamento determinístico documentado.
- **Idempotência** em pagamento, estorno, fechamento e webhooks: repetir a chamada não duplica efeito.
- **Concorrência segura:** dois dispositivos na mesma comanda, ou pagamento simultâneo, não perdem nem duplicam valor (bloqueio otimista/pessimista definido por operação).
- **Conciliação e totais de controle:** soma dos pagamentos = saldo da comanda; saldo do caixa = movimentos; saldo de estoque = soma das movimentações. Divergência gera alerta.
- **Trilha completa** de cancelamentos, cortesias, descontos e estornos com ator, motivo, valor e limite aplicado; relatórios de exceção (quem mais cancela, desconta, dá cortesia).
- **Separação de funções:** quem abre caixa, quem aprova diferença e quem altera permissão podem ser pessoas distintas por configuração.

## S11. Segurança de API e aplicação

- **Validação de entrada** estrita por esquema em toda fronteira (tipos, tamanhos, formatos, listas permitidas); **nunca** montar consulta por concatenação; ORM/consultas parametrizadas.
- **Saída codificada** contra XSS; Content Security Policy restritiva; sem HTML de usuário sem sanitização.
- **Proteção contra:** SQL/NoSQL injection, XSS, CSRF, SSRF (chamadas a URLs fornecidas pelo usuário/webhooks passam por lista de destinos permitidos), desserialização insegura, path traversal, mass assignment, abuso de lógica de negócio.
- **Autorização por objeto em toda rota** (anti-IDOR), com teste.
- **Rate limit e cotas** por IP, usuário, dispositivo e organização; limites de tamanho de corpo, de paginação e de tempo de consulta.
- **Upload de arquivo** (NF-e XML, anexos, imagens): validação de tipo real, tamanho, varredura de malware, processamento isolado, armazenamento fora do domínio da aplicação, URLs assinadas e temporárias, **análise segura de XML** (sem entidades externas, anti-XXE).
- **Webhooks** (pagamento/fiscal): assinatura verificada, proteção contra replay, idempotência.
- **Cabeçalhos de segurança** e CORS restrito; respostas de erro sem detalhes internos.
- **Versionamento e depreciação** de API controlados; nada de endpoint "de teste" em produção.

## S12. Mitigações compensatórias enquanto não há 2FA

Como 2FA está fora (risco aceito), estes controles são **obrigatórios**:
- Senha forte + verificação de vazamento + bloqueio progressivo.
- **Alerta imediato** (todos os canais) de login em dispositivo ou local novo, para o próprio usuário e para o dono.
- Control Tower: **restrição por IP/rede ligada desde o início, aplicada na API** (front em `/control` no início; host separado previsto depois) (lista de endereços permitidos configurável pelo super admin), limites mais duros de tentativa, alerta ao super admin de qualquer login de administrador.
- Auditoria imutável com cópia fora do banco principal.

### Preparado, desligado por padrão (decisão: sem atrito agora)
O motor de segurança já suporta, como **política configurável**, sem refazer nada:
- **Reautenticação por senha em ações de alto risco** (alterar permissões, exportar dados, mexer em métodos de pagamento, impersonar, alterar dados fiscais). Cada ação pode ser marcada como "exige reautenticação"; hoje nenhuma está.
- **2FA** (TOTP), exigível por organização, por perfil ou para a Control Tower.
- **Tempos de sessão** diferentes por perfil (ver S7).
Ligar qualquer um é mudança de configuração, não de código.

## S13. Control Tower e impersonação

- Domínio de identidade **próprio** (D14): tabelas, tokens e permissões separados; audiência de token distinta. **Front em `/control`** no início (mesma origem do app, risco aceito com CSP estrita, cookies próprios e carregamento sob demanda; ver `04` A10.3); extração para host próprio prevista.
- Super admin: conta protegida, sem exclusão/rebaixamento por terceiros; criação e alteração de administradores é ação auditada e exige reautenticação.
- **Impersonação** (D15): sessão claramente marcada; todo ato atribuído ao admin real; organização sempre informada; política por administrador (leitura/escrita, tempo, motivo) aplicada no servidor; sem acesso a segredos (hash de senha, PIN, certificado fiscal, chaves) **mesmo para o super admin** (impersonar é ver/operar como usuário, não ler segredo).
- Acesso da plataforma ao banco de produção só por canal controlado e auditado (acesso emergencial com registro), nunca por credencial compartilhada.
- Exportação de dados de cliente pela plataforma é permissão específica, auditada e visível à organização.

## S14. Auditoria e detecção

- **Log de auditoria imutável** (append-only, encadeado por hash, cópia fora do banco principal), cobrindo as ações **[A]**, autenticação, mudanças de permissão, exportações, acessos negados, impersonação, configurações e acessos a dados pessoais.
- **Separado** do log técnico; ambos com id de correlação e sem segredos.
- **Detecção de anomalias:** muitos cancelamentos/descontos por usuário, acessos fora do horário, picos de exportação, falhas repetidas, acesso negado em série, login novo, uso incomum de impersonação. Gera alerta ao dono/administrador conforme escopo.
- Verificação periódica da integridade da cadeia de auditoria; falha de integridade é incidente.

## S15. Infraestrutura e cadeia de suprimentos

- Princípio do menor privilégio em contas de nuvem e serviços; **sem credenciais de longa duração** onde houver identidade federada; separação de ambientes e de contas.
- Rede: banco de dados **nunca exposto à internet**; entrada única por borda protegida (WAF, rate limit, DDoS); comunicação entre serviços autenticada.
- **Infraestrutura como código**, revisada e versionada; mudanças manuais proibidas.
- **Dependências:** inventário (SBOM), verificação de vulnerabilidades no CI, versões fixadas e atualizadas com rotina, revisão de pacotes novos; imagens e artefatos assinados; build reproduzível em ambiente isolado.
- **Pipeline protegido:** revisão obrigatória, branch protegida, segredos de CI mínimos, sem execução de código de PR externo com segredos.
- **Patch de segurança** com prazo por severidade.

## S16. Resposta a incidentes

- Plano escrito: detecção, contenção, erradicação, recuperação, comunicação e lições aprendidas.
- **Botões de emergência** na plataforma: revogar todas as sessões de uma organização, de um usuário ou de todos; suspender organização; desligar impersonação; rotacionar segredos.
- Notificação a organizações afetadas e à ANPD nos prazos legais (RNF5).
- Retenção de evidências; exercício de simulação periódico.
- Programa de divulgação responsável (contato de segurança publicado).

## S17. Verificação contínua

| Atividade | Frequência |
|---|---|
| Testes automáticos de isolamento e matriz de permissões | Todo merge |
| SAST, verificação de dependências e de segredos | Todo merge |
| DAST em homologação | A cada release |
| Revisão de segurança de funcionalidades sensíveis (pagamento, permissões, importação, impersonação) | Antes de liberar cada uma |
| Teste de invasão externo | Antes do primeiro cliente pagante e anual |
| Exercício de restauração de backup | Mensal |
| Revisão de acessos e de permissões da plataforma | Trimestral |
| Revisão do modelo de ameaças | A cada mudança de arquitetura relevante |

## S18. Critérios de aceite de segurança (portão antes de aceitar clientes pagantes)

1. Testes de isolamento entre organizações cobrem 100% das rotas e passam.
2. Matriz de permissões dos perfis de fábrica validada e automatizada.
3. Auditoria imutável operando, com verificação de integridade.
4. Segredos em cofre, varredura limpa, rotação testada.
5. Backups com restauração testada dentro do RTO.
6. Alertas de login novo e de anomalia ativos.
7. Teste de invasão externo concluído e achados críticos/altos resolvidos.
8. **Decisão sobre 2FA registrada:** lançamento sem 2FA (risco aceito, com as mitigações do S12 ativas e o encaixe pronto para ligar).
9. Plano de incidentes e botões de emergência testados.
10. Termos, política de privacidade e acordo de tratamento de dados publicados.

## Decisões tomadas nesta rodada
- Reautenticação em ações de alto risco: **preparada, desligada** (S12).
- Sessões: token de acesso curto e invisível; tempos da sessão configuráveis, padrão generoso (S7).
- Control Tower: **restrição por IP ligada desde o início**.
- Segredos (hash de senha, PIN, certificado fiscal, chaves) **inacessíveis até para o super admin** (S13).
- **Sem 2FA no lançamento**; encaixe pronto.
