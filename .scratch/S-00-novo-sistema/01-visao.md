# Novo sistema — Visão

Fatia: S-00 · Aberta: 2026-10-21 · Fechada: 2026-11-02 · Tickets: planejamento (sem ticket)

Status: rascunho em construção (decisões fechadas em conversa, 2026-10-21).

## Contexto

Reescrita do zero do sistema de gestão para bar/restaurante (Matchpoint / Flow4Tech). O sistema atual não é referência: não extrair regras dele, planejar a partir do que o produto deve ser.

## Por que reescrever

- Difícil adicionar features sem quebrar o que existe.
- Segurança fraca.
- Não escala.
- Frontend ruim (feio, inconsistente).
- Dívida técnica de arquitetura.

## Decisões fechadas

| # | Decisão |
|---|---|
| D1 | Reescrita total. Backend, frontend e UX refeitos. Stack aberta para reavaliação. |
| D2 | Cliente inicial: um bar/restaurante independente, com um usuário hoje. Arquitetura preparada para crescer desde o dia zero. |
| D3 | Hierarquia base: **Organização → Unidades → Usuários**. Uma organização tem N unidades e N usuários. Hoje: 1 organização com 1 unidade. |
| D4 | Permissões de nível exemplar, inspiradas no modelo do AWS IAM: toda ação tem uma permissão, tudo configurável e personalizável por quem administra. |
| D5 | Segurança é requisito central do plano, não etapa final. |
| D6 | Feature-friendly: novas features entram sem regressão. Isso exige forte cobertura de testes e módulos com fronteiras claras. |
| D7 | Offline fora de escopo por enquanto. |
| D8 | Migração de dados do sistema atual fora do foco do produto. Construir de modo que seja possível depois; o plano da migração do restaurante atual está em `08-migracao-sistema-atual.md`. |
| D9 | Time e prazo não restringem o plano (desenvolvimento assistido por IA). |
| D10 | Permissões com **condições e limites de valor** (ex.: desconto até X% por perfil), além de allow/deny granular e escopo por unidade. Modelo completo previsto desde o início. |
| D11 | Catálogo de permissões **derivado do catálogo de features**: cada feature declara suas permissões, ações sensíveis e limites. Planejar features junto com a arquitetura. |
| D12 | Plataforma de administração **separada** do sistema dos clientes: a **Control Tower** (`/control`), com identidade, login e modelo de permissões próprios. Existe um **administrador supremo** (super admin) que cria outros administradores com permissões delegadas. |
| D13 | Dois tipos de conta: com e-mail (dono, gerentes) e operacional sem e-mail (usuário + senha, garçom e caixa). Login com Google incluído. Ninguém define a senha de outra pessoa: convite por link ou credencial temporária de troca obrigatória. |
| D14 | Control Tower é **separação lógica de identidade** (esquema, tokens e permissões próprios), no mesmo deploy do sistema. Front em rota `/control` no início, com extração para host próprio prevista. Sem infraestrutura dupla. |
| D15 | Impersonação: super admin sem restrições; demais administradores com política configurável por pessoa (leitura/escrita, tempo, motivo). Cliente sempre informado e atribuição sempre ao admin real. |
| D16 | **Canal principal de lançamento é o app do garçom no celular**; a versão web é a central de visualização e gestão das comandas (com edição possível, mas secundária). Lançar um pedido deve levar poucos toques: o cliente do restaurante não tem tempo de marcar na comanda em horário de pico. Origem: conversa com o cliente. |
| D17 | **Preparar para tudo, configurável:** o sistema funciona em Android, iPhone e tablet (aparelho próprio do garçom ou do restaurante), em **tema claro e escuro** (troca manual e automática), e recursos como **mesas, clientes e fiado** são ligados ou desligados por organização e unidade. Nenhum depende de resposta prévia do cliente. |

## Fora de escopo (por ora)

- Operação offline / PWA com sync.
- Migração de dados do sistema legado.
- 2FA (modelo de autenticação deixa o encaixe pronto).
- Delivery, integração com marketplaces (iFood) e robô de WhatsApp: sem previsão.
- Open Finance e conciliação bancária automática: fora de escopo (custo recorrente de intermediário). Integração direta com sistema contábil também só sob demanda.

## Riscos aceitos

| Risco | Decisão |
|---|---|
| Sem 2FA, nem para dono nem para a Control Tower | Aceito para o lançamento. Um super admin sem 2FA com impersonação irrestrita é o ponto único de comprometimento de todas as organizações; por isso as mitigações de `03-seguranca` S12 são obrigatórias (senha forte, bloqueio progressivo, alerta de login novo, restrição por IP na Control Tower, auditoria imutável). O encaixe de 2FA e de reautenticação em ações de risco está preparado e se liga por configuração. |
| Super admin impersona sem motivo e sem limite de tempo | Aceito. Auditoria e aviso à organização continuam obrigatórios. |

## Questões em aberto

- Granularidade e modelo das permissões (ver 03-seguranca).
- Produtos/módulos novos além do núcleo operacional.
- Stack e arquitetura (sai dos requisitos, vem depois).
