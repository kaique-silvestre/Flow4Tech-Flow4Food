# Novo sistema — Visão

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
| D8 | Migração de dados do sistema atual fora do foco. Construir de modo que seja possível depois. |
| D9 | Time e prazo não restringem o plano (desenvolvimento assistido por IA). |

## Fora de escopo (por ora)

- Operação offline / PWA com sync.
- Migração de dados do sistema legado.

## Questões em aberto

- Granularidade e modelo das permissões (ver 03-seguranca).
- Produtos/módulos novos além do núcleo operacional.
- Stack e arquitetura (sai dos requisitos, vem depois).
