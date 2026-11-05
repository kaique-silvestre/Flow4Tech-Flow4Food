# 013. Configurações em camadas declaradas em código, histórico só de acréscimo

- Status: accepted (ticket 10 da plataforma base, spec 5.7)
- Date: 2026-11-05

## Context
Tempos de sessão, política de bloqueio e (depois) regras operacionais precisam variar por organização e por unidade, com histórico, e uma mudança não pode reescrever o passado (F14.3). O catálogo de permissões já provou o padrão "declarado no código, coletado automaticamente".

## Decision
1. **Opções declaradas em código** (`SettingDefinition` em classe `[SettingsCatalog]`, Kernel): chave `grupo.nome`, tipo, padrão, camadas em que vale (`SomenteSistema`, `Organizacao`, `OrganizacaoEUnidade`), limites, `ConfigPath`, `RequiresEffectiveInstant`. Coletadas dos assemblies dos módulos, validadas na partida e exportadas para `backend/settings.catalog.txt` (chave publicada nunca é removida nem renomeada, como o catálogo de auditoria).
2. **Resolução** sistema > organização > unidade, pura (`ResolvedorDeConfiguracao`). A camada de sistema é o padrão declarado sobrescrito pela configuração do ambiente (as variáveis antigas continuam funcionando); valor inválido na configuração impede o host de subir. A organização pode **travar** a opção; uma camada de unidade exige a da organização. Sem camada por perfil (a spec 5.3.4 cita "por perfil"; fica fora).
3. **Armazenamento `configuracoes_versoes`** (migração 0014): tenant, RLS exata, **só de acréscimo por privilégio** (como a auditoria). Uma linha = uma versão de uma opção num escopo; `valor` nulo = herdar; `vigente_a_partir_de >= criado_em` é CHECK (**a mudança nunca alcança o passado, garantido pelo banco**). Versão em vigor num instante = a de maior número entre as já iniciadas, então uma versão gravada depois cancela uma agendada que ainda não começou. Restaurar = nova versão que copia a antiga. Quem escreve o mesmo escopo e chave serializa por advisory lock.
4. **Alterar só por `Configuracoes`:** uma unidade de trabalho que pergunta ao motor `organizacao:configurar` (adaptado em `IPermissaoDoChamador`), valida camada, valor, trava e instante, acrescenta a linha e audita (`configuracao:alterar|restaurar`, antes/depois). A camada de unidade é a unidade ATIVA da sessão (o cliente nunca a nomeia). Leitura por outros módulos: `ISettings` com instante explícito.
5. **Consumidores:** os dois relógios de sessão (ADR-010) leem por organização ao iniciar a sessão e ao deslizar a janela; o limite absoluto é fixado ao iniciar. `bloqueio.*` é só de sistema (ADR-011). O resolvedor do dia operacional **não** lê as opções `dia-operacional.*` ainda (roadmap 2.10b).

## Consequences
- Mais fácil: histórico e "restaurar" saem do modelo de versões; "não retroagir" é do banco, não de disciplina; nova opção = uma declaração.
- Aceito: sem cache (uma leitura a mais quando a sessão desliza a janela, no máximo uma vez por intervalo por sessão); sem camada de perfil; durações de credencial temporária, convite e recuperação ainda são opções de `IdentidadeOptions`, não configurações; uma mudança de tempo de sessão só alcança sessões que começam depois.

## Alternatives considered
- **Tabela de valores atuais com histórico em tabela à parte (duas escritas):** o histórico pode divergir do valor; a versão única é a fonte.
- **Reaproveitar `IConfiguration` com sobreposição por organização:** não tem histórico, vigência nem RLS.
- **Cache com invalidação por evento:** adiado até haver custo medido.
