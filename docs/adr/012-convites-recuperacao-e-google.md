# 012. Convites e recuperação por token de uso único com organização como localizador; Google por OIDC sem criar conta

- Status: accepted (ticket 09 da plataforma base, spec 5.2.1, 5.2.3, 5.2.5, 5.2.6)
- Date: 2026-11-05

## Context
Convite e recuperação são fluxos **anteriores** ao contexto de organização: a pessoa chega com um link. A spec (5.2.4) previa estender a pré-autenticação por `SECURITY DEFINER` (ADR-005, item 6). Cada função `SECURITY DEFINER` é uma porta deliberada que ignora o RLS; quanto menos, melhor. O login com Google acrescenta um provedor externo que não pode criar contas nem confundir contas.

## Decision
1. **O token carrega a organização como localizador**: convite `c1.<org 32 hex>.<256 bits>`, recuperação `a1.<...>`. Só o SHA-256 do texto inteiro é guardado (`convites`, `recuperacoes`, migração 0013, tenant, RLS exata). A busca por hash roda **sob o RLS da organização do token**; token com organização trocada não acha nada. **Não foi criada nenhuma função `SECURITY DEFINER` nova nem tabela sem `organizacao_id`**: o desenho de pré-autenticação do ADR-005 não mudou, só ficou menor do que a spec previa. O localizador não é segredo (o segredo são os 256 bits); descobrir a organização não ajuda.
2. **Uso único por UPDATE condicional** (`usado_em IS NULL AND expira_em > agora`): vencido, usado, revogado, desconhecido e malformado dão a mesma resposta. O token só existe no e-mail (enviado depois do commit, fragmento `#token=` no link); nunca no outbox, na auditoria nem no log. Falha do provedor de e-mail vira log sem endereço.
3. **Convite:** um pendente por e-mail (o novo revoga o antigo); a pessoa escolhe a PRÓPRIA senha ao aceitar; o e-mail só entra em `identidade_emails` ao aceitar (sem reserva de e-mail sem prova de posse). Perfil e unidade ficam no convite e vão no evento `identidade.convite-aceito`.
4. **Recuperação:** `SolicitarAsync` não devolve nada e a rota responde 202 sempre; espera o piso de tempo do login e conta cada pedido no `ILimitadorDeLogin` (ADR-011). Redefinir consome o token atomicamente, troca a senha e encerra todas as sessões (versão da credencial). Conta operacional não tem e-mail: quem tem `usuario:redefinir-acesso` gera nova credencial temporária (uma vez, 72 h, nunca a própria).
5. **Google (OIDC), `ContasGoogle` atrás de `IProvedorOidc`:** código + PKCE S256, `state` e `nonce` em cookie assinado (`__Host-f4t_oidc`, `SameSite=Lax`, chave derivada de `AppKey`), id token validado (RS256 fixo, `kid`, emissor, audiência, validade por `IClock`, nonce), `sub` + `email_verified`. **Nunca cria conta.** O vínculo só é feito pela pessoa já logada, com a senha atual e e-mail do Google verificado e igual ao da conta (`identidades_externas`, `UNIQUE (provedor, sub)`); entrar exige o `sub` vinculado; todo o resto devolve a mesma resposta neutra. Conta operacional não usa Google (premissa, questão 6): recusada no serviço e no banco (FK composta com o tipo `email`).

## Consequences
- Mais fácil: nenhuma exceção nova ao RLS; o teste do conjunto exato de funções `SECURITY DEFINER` continua valendo; o fluxo inteiro roda sob a isolação normal.
- Aceito: perfil e unidade do convite não são aplicados ao aceitar ainda (o evento não tem consumidor, e aplicar deve usar os direitos de quem convidou, `GrantGuard`); o e-mail de uma conta desativada não é liberado; dois convites simultâneos ao mesmo e-mail podem dar 500 em vez de 409; o Google real nunca foi chamado (testes contra provedor falso com RSA de teste); sem ClientId as rotas dão 404.

## Alternatives considered
- **Função `SECURITY DEFINER` para localizar o token por hash:** mais uma porta que ignora o RLS, sem ganho: a organização no token já resolve.
- **Criar conta no primeiro login com Google:** rejeitado pela spec (5.2.3); abre a porta para tomada de conta por e-mail igual.
- **Vincular por e-mail igual sem senha atual:** rejeitado; quem controla o e-mail do provedor não é necessariamente dono da conta.
