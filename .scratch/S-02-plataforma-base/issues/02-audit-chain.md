# 02: Auditoria imutável encadeada e log técnico com correlação

**What to build:** Tabela de auditoria append-only por organização, hash encadeado, escrita na mesma transação da ação, verificação de cadeia, log técnico estruturado com id de correlação. Spec 5.6.
**Blocked by:** 01
**Touches:** `backend/src/Kernel/**`, `backend/src/Infrastructure/**`, `backend/migrations/**`, `backend/tests/**`
**Nature:** objective
**tdd-scope:** required
**Seams:** RLS, Outbox, Unit of work
**Status:** done-local

- [x] `UPDATE` e `DELETE` negados à credencial da aplicação por privilégio, com teste (`AuditTrailIntegrationTests.The_application_credential_cannot_update_delete_or_truncate_the_trail`: SQLSTATE 42501 para UPDATE, DELETE e TRUNCATE; `The_privileges_are_exactly_select_and_insert_for_the_application_role`; migração `0005_auditoria.sql` faz o REVOKE)
- [x] Adulterar, remover ou reordenar um registro é detectado pela verificação de cadeia (`AuditChainVerificationIntegrationTests`: 9 adulterações de coluna, remoção no meio e do primeiro, troca de posições, registro movido; adulteração feita com a credencial OWNER; cauda removida só com âncora, limite documentado)
- [x] Registro escrito na mesma `IUnitOfWork` da ação; ação [A] sem registro falha o teste (`When_the_action_throws_the_record_disappears_with_it`, `A_switch_that_fails_after_being_audited_leaves_no_audit_record`, `Switching_unit_writes_an_audit_record...`; o gate genérico "toda ação [A] tem registro" depende do catálogo [A] do ticket 06 e NÃO existe ainda)
- [x] Mesma id de correlação na requisição, no log técnico e na auditoria (`RequestContextEndpointsTests.Switching_unit_writes_an_audit_record_with_actor_before_after_and_the_correlation_id...`: `traceparent` enviado = `X-Correlation-Id` = linha `http.requisicao` = `correlation_id` do registro)
- [x] Varredura de log e auditoria não acha senha, token nem hash de senha (`SensitiveDataTests`, `AuditTrailIntegrationTests.A_secret_in_before_or_after...` rejeita e nada é gravado, `TechnicalLogTests` mascara, `The_technical_log_of_a_request_never_carries...`; achado real: o log do próprio ASP.NET imprimia a query string com `?token=`, silenciado em `Program.cs`)
- [x] Tabela passa pelo teste de arquitetura de RLS (`RlsArchitectureIntegrationTests` sem alteração: política exata, FORCE; append-only vem do privilégio, não de política extra)

## Decisões e limites (implementação local)
- Implementação em `src/Infrastructure/Audit` (projeto novo, como Outbox: EF sobre `TenantDbContext`, gate de arquitetura ajustado só para ele) e contratos em `Kernel/Audit` (`IAuditLog`, `AuditEntry`, `ITechnicalLog`, `SensitiveData`, `Correlation`). `IRequestContext` ganhou `SessionId`.
- Serialização por organização: `pg_advisory_xact_lock` por organização dentro da transação; `UNIQUE (organizacao_id, sequencia)` como rede. O teste de concorrência (24 escritores) foi visto falhar com 23505 quando o lock é removido.
- Hash: SHA-256 de JSON canônico versão 1 (ordem fixa de campos, chaves ordenadas, números normalizados, instante em microssegundos). Documentado em `AuditHash`.
- Falha deliberada: `RecordDetachedAsync` (transação própria), recusa rodar dentro de uma unidade de trabalho (evita impasse no lock da cadeia).
- Catálogo de ações versionado em `backend/audit.catalog.txt`, só cresce (regenerar nunca remove).
- NÃO provado/feito: agendamento periódico real do job (só o job e o handler, testados); consumidor do evento `auditoria.integridade-violada`; cópia da cadeia fora do banco (dívida da spec); cauda removida sem âncora é indetectável; dono do banco que recalcula todos os hashes só é pego com cópia externa; tabela `auditoria_registros` sem FK para usuário/sessão (tickets 03/04); retenção/particionamento; custo do lock sob carga alta numa mesma organização não medido.

## Correções da revisão independente (commit dfcfdcd)
- Alerta deduplicado: `auditoria.integridade-violada` sai uma vez por violação DISTINTA (impressão digital do conjunto de violações guardada por organização); só repete se o conjunto mudar ou se a cadeia voltar a ser válida e quebrar de novo. Execuções concorrentes serializam na linha de estado (`FOR UPDATE`).
- Âncora persistida: migração `0006_auditoria_verificacao.sql` (tabela de tenant, RLS exata; a aplicação faz INSERT/UPDATE, sem DELETE/TRUNCATE; `auditoria_registros` segue append-only). Cada verificação VÁLIDA avança a âncora (sequência + hash do head); verificação inválida não a move. O job periódico (sem âncora explícita) usa a guardada e passa a detectar cauda removida. Âncora explícita tem precedência.
- Número fora do alcance de `decimal`: `AuditValueException` (validação tipada, só o caminho, nunca o valor) em `AuditEntryRules` e em `AuditHash`.
- `SensitiveData`: chaves `accesskey`, `passcode` por conteúdo; `pass`, `pin`, `otp`, `jwt` como palavra inteira da chave (desvio consciente: por conteúdo, `pin` pegaria `shipping`/`mapping` e `pass` pegaria `passageiro`); nome de chave com cara de segredo (`LooksLikeSecret`) também vale e nunca é ecoado no caminho do achado.

## Riscos (revisão independente)
- **Limite da âncora (item 2):** a âncora fica no MESMO banco. Protege contra quem remove registros mas não reescreve também `auditoria_verificacao`; quem tem a credencial OWNER pode ajustar a âncora junto. A primeira verificação de uma organização não tem âncora. A cópia externa (fora do banco) segue como dívida da spec (questão em aberto 3).
- **Sessão real no ticket 04 (revisor, item 3):** quando a troca de unidade passar a alterar a sessão real (ticket 04), essa mudança de sessão DEVE entrar na mesma unidade de trabalho da troca de unidade e do registro de auditoria; hoje a sessão é só contexto, então o risco ainda não existe. NÃO implementado aqui.
- **Ordem de lock (revisor, item 4):** o contador de limite de consumo (`limites_consumo`, ticket 09) deve ser travado ANTES do lock da cadeia de auditoria na mesma unidade, para manter uma ordem global única e evitar impasse. Hoje não há gate que verifique essa ordem, e o custo do lock da cadeia sob carga numa mesma organização não foi medido. NÃO implementado aqui.
