# 017. Módulo Clientes: cadastro com consentimento, duplicidade, mascaramento e esquecimento (LGPD)

- Status: proposed (ticket F4-0147; as decisões marcadas "decisão do agente" esperam a confirmação do dono)
- Date: 2026-11-23

## Context
O S-05 (bloco G4, histórias 16 e 17) pede cadastro e busca de clientes com consentimento e aviso de duplicidade, vínculo opcional à comanda e dados pessoais mascarados para quem não tem a permissão (LGPD). O nome `ADR-017` já era citado desde o 2A como o ADR de Clientes e Promoções, que ficou sem escrever porque nenhum dos dois módulos existia. Fiado e voucher (F4-0148) dependem deste módulo.

## Decision
Módulo novo `Clientes` (Domain, Application, Infrastructure, Interface), sem dependência de outro módulo; `Comandas` passa a depender dele (`modules.dependencies.json`), só pela porta pública `IConsultaDeVinculo` (o cliente existe na organização, está ativo e com consentimento vigente), sem ler dado pessoal.

- **Tabela `clientes`** (migração 0035, tenant, RLS exata): nome, telefone, e-mail e documento opcionais, guardados NORMALIZADOS (telefone e documento só dígitos, e-mail em minúsculas), de modo que a duplicidade é uma igualdade simples. O consentimento faz parte da linha: concedido, quando, finalidade (código), versão do texto, quem registrou, quando foi revogado. Estado `ativo` ou `anonimizado`. O app não tem DELETE: cliente nunca é removido, só anonimizado.
- **Consentimento explícito.** O cadastro só segue com `consentimento.concedido = true`; ausente ou falso é 400 `consentimento-obrigatorio`. A finalidade é um código (`[a-z0-9-]{3,40}`) e a versão do texto um token curto: alfabeto fixo, porque o texto livre do cliente não entra em nenhum registro.
- **Duplicidade avisa, não bloqueia.** Mesmo telefone normalizado, mesmo e-mail ou mesmo documento de outro cliente ativo devolve 409 `possivel-duplicidade` com os candidatos; o cadastro só grava com `confirmarDuplicidade = true`. Vale também na edição, só para o contato que mudou. Não é restrição do banco: dois cadastros confirmados ao mesmo tempo passam.
- **Mascaramento por permissão.** `cliente:ver` abre lista, busca e detalhe; `cliente:ver-dados-pessoais` decide, no caso de uso, se telefone, e-mail e documento saem em claro ou mascarados (`***-**12`, `j***@***`). O nome nunca é mascarado. A máscara vale em toda resposta que devolve cliente, inclusive a lista de candidatos da duplicidade e a resposta do próprio cadastro.
- **LGPD.** `POST /clientes/{id}/revogar-consentimento` marca o consentimento como revogado: o cliente fica, mas não recebe vínculo novo nem edição. `POST /clientes/{id}/anonimizar` (`cliente:anonimizar`, rota sensível: a negação é auditada) apaga nome e contatos e mantém a linha, a prova do consentimento, o vínculo com comandas e o histórico financeiro; é irreversível e idempotente só no sentido de recusar a segunda vez (409 `anonimizado`).
- **Vínculo à comanda.** Coluna opcional `comandas.cliente_id` com chave composta `(organizacao_id, cliente_id)`: o banco recusa cliente de outra organização. `PUT /comandas/{id}/cliente` (`cliente:vincular`) liga ou desliga, confere a versão da comanda como as outras edições e audita só ids.
- **Auditoria sem dado pessoal.** Ações novas `cliente:cadastrar|editar|revogar-consentimento|anonimizar` e `comanda:vincular-cliente`; Before/After levam ids, flags e NOMES de campo (`campos`, `camposApagados`), nunca nome, contato ou documento (a `SensitiveData` recusaria chaves suspeitas; a regra aqui é mais estrita).
- **Perfis modelo (decisão do agente, dono não confirmou).** Garçom: ver, cadastrar e vincular, sem dados pessoais. Caixa: também editar e ver dados pessoais (vai cobrar fiado). Gerente: tudo, inclusive anonimizar. O dono tem tudo pelo perfil de fábrica. Nenhuma dessas permissões tem limite.

## Consequences
Fiado e voucher (F4-0148) podem referenciar `clientes.id` com chave composta e consultar o módulo pela mesma porta. O cliente anonimizado continua nas comandas e pagamentos antigos com o nome "Cliente anonimizado". Quem só pode cadastrar vê candidatos mascarados, mas o nome do candidato aparece (o nome nunca é mascarado). Não há verificação dos dígitos do CPF/CNPJ nem validação plena de e-mail: o contato se confirma pelo uso.

## Alternatives considered
- **Tabela de consentimentos separada (histórico de concessões e revogações).** Mais fiel a uma auditoria jurídica longa, mas o 2B só precisa da prova do consentimento corrente; o histórico fica na trilha de auditoria. Reabrir se o dono pedir reconsentimento por versão do texto.
- **Restrição única no banco para telefone, e-mail e documento.** Recusaria irmãos que dividem telefone e impediria o "cadastrar mesmo assim" que o dono pediu.
- **Apagar a linha ao esquecer.** Quebraria o histórico financeiro e a chave composta das comandas.
- **Mascarar no front.** O dado já teria saído do servidor; a decisão é do servidor.
