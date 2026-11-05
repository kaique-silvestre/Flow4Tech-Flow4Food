# 011. Proteção de login com estado em memória do processo, alertas pelo outbox

- Status: accepted (ticket 08 da plataforma base, spec 5.5); limites conhecidos abaixo
- Date: 2026-11-05

## Context
O login precisa resistir a tentativa em massa (senha por conta, mesma senha em muitas contas, muitos endereços contra uma conta) sem revelar se a conta existe (ADR-005, item 9) e sem abrir um novo caminho de negação de serviço. A decisão do bloqueio acontece **antes** de qualquer busca ou hash, portanto antes de existir organização; e um endereço IP não pertence a nenhuma organização.

## Decision
1. **Porta `ILimitadorDeLogin`** (Application) com implementação em memória (`LimitadorDeLoginEmMemoria`): bloqueio progressivo por conta **e** por endereço (`base * 2^(falhas - limite)` até um teto), limites distintos para conta com e-mail e operacional, detecção de credential stuffing (muitas contas de um endereço; muitos endereços contra uma conta) e CAPTCHA adaptativo (`ICaptcha`, sem provedor: nunca é pedido).
2. **A chave é o identificador DIGITADO, nunca a conta encontrada.** Nome inexistente bloqueia exatamente como o existente; tentativa bloqueada não faz hash, não é auditada e ainda espera o piso de tempo. Resposta: 429 com `Retry-After`, ou 428 quando o CAPTCHA é exigido.
3. **Política só de sistema.** Os limites `bloqueio.*` são opções da camada de sistema (ADR-013): o limitador julga antes de saber a organização, então uma camada por organização não tem de onde ser lida.
4. **Estado em memória, por instância, limitado** (`MaximoDeRegistros`). Trocar por um armazenamento compartilhado muda a porta, não os chamadores. A recuperação de acesso (ADR-012) reutiliza o mesmo limitador com outra chave.
5. **Alertas pelo outbox, na mesma unidade de trabalho da ação:** `seguranca.login-novo` (aparelho = agente sem dígitos, lugar = prefixo de rede /24 ou /64, tabela `acessos_conhecidos`, máximo 50 por conta e tipo; o primeiro login só grava a linha de base), `seguranca.conta-bloqueada` (+ auditoria `autenticacao:bloquear`, só conta conhecida), `seguranca.senha-trocada`, `seguranca.acesso-recuperado`, `seguranca.refresh-reutilizado`. Consumidores idempotentes mandam e-mail à pessoa e aos donos ativos. O bloqueio de um endereço vai ao log técnico com o endereço reduzido a 8 hex.

## Consequences
- Mais fácil: nenhuma tabela nova no caminho quente do login; testável sem banco (`LimitadorDeLoginTests`, `AutenticacaoProtegidaTests`); sem enumeração por bloqueio.
- Aceito: o estado some no reinício e não é compartilhado entre instâncias (um atacante distribui tentativas por instâncias e reinícios); atrás do Railway é preciso ligar `Flow4Tech__Rede__ProxyConfiavel`, senão o endereço é o do proxy; um atacante pode bloquear uma conta alheia digitando o nome dela (negação de serviço dirigida, limitada pelo teto de 15 minutos). Não existe e-mail real nem provedor de CAPTCHA (questão aberta 4); o despachante do outbox ainda não está agendado, então nenhum alerta sai em produção.
- Trocar para estado compartilhado é obrigatório antes de rodar mais de uma instância da API.

## Alternatives considered
- **Contador em tabela do banco:** compartilhado e durável, mas põe escrita no caminho de toda falha, antes de existir organização (sem RLS possível) e dá ao atacante um amplificador de escrita.
- **Bloquear pela conta encontrada:** revela quais contas existem (bloqueio só em nome real); rejeitado.
- **Atraso fixo sem bloqueio:** não limita taxa; só encarece.
