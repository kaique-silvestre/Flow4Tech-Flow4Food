using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Flow4Tech.Cardapio.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Cardapio.Application;

/// <summary>
/// Menu import from a spreadsheet (F4-0069). Who may call is the route's job (<c>cardapio:importar</c>). The preview reads and plans and writes nothing. The apply is ONE unit of work: the whole sheet
/// is validated again on the server, and either every row applies (rows, the import record, one audit record with counts only, one event) or none does. It is idempotent by import id: a repeat
/// with the same file answers the first result without writing; the same id with another file is refused. Changing the price of an existing product still asks the engine for
/// <c>cardapio:preco-alterar</c> with the percent it moves (the import is not a way around that limit).
/// </summary>
public sealed class GestaoDaImportacao(
    IUnitOfWork unitOfWork,
    ILeitorDePlanilha leitor,
    ICardapioStore cardapio,
    IOpcoesStore opcoes,
    IImportacoesDoCardapioStore importacoes,
    IAuditLog audit,
    IEventosDoCardapio eventos,
    IDecisaoDePreco precos,
    IClock clock)
{
    public Task<ResultadoDaValidacao> ValidarAsync(IRequestContext request, FormatoDePlanilha formato, byte[] conteudo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(conteudo);

        // The file is untrusted and is read BEFORE the unit of work opens: a slow or hostile sheet never holds a tenant transaction.
        var leitura = leitor.Ler(formato, conteudo);
        if (leitura.Erro != ErroDeLeitura.Nenhum)
        {
            return Task.FromResult(new ResultadoDaValidacao(ResultadoDaImportacao.ArquivoInvalido, leitura));
        }

        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var plano = RegrasDaImportacao.Planejar(leitura.Linhas, await EstadoAtualAsync(request.Organization.OrganizationId, request.Organization.UnitId, ct));
                return new ResultadoDaValidacao(plano.Valido ? ResultadoDaImportacao.Ok : ResultadoDaImportacao.ComErros, leitura, plano);
            },
            cancellationToken);
    }

    public async Task<ResultadoDaAplicacao> AplicarAsync(IRequestContext request, Guid importacaoId, FormatoDePlanilha formato, byte[] conteudo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(conteudo);
        if (importacaoId == Guid.Empty)
        {
            return new ResultadoDaAplicacao(ResultadoDaImportacao.Invalido);
        }

        var hash = Convert.ToHexString(SHA256.HashData(conteudo));
        var leitura = leitor.Ler(formato, conteudo);
        try
        {
            return await unitOfWork.ExecuteAsync(
                request,
                async ct =>
                {
                    var org = request.Organization.OrganizationId;
                    if (await importacoes.AcharAsync(org, importacaoId, ct) is { } anterior)
                    {
                        return Repetida(anterior, hash);
                    }

                    if (leitura.Erro != ErroDeLeitura.Nenhum)
                    {
                        return new ResultadoDaAplicacao(ResultadoDaImportacao.ArquivoInvalido, Leitura: leitura);
                    }

                    var plano = RegrasDaImportacao.Planejar(leitura.Linhas, await EstadoAtualAsync(org, request.Organization.UnitId, ct));
                    if (!plano.Valido)
                    {
                        return new ResultadoDaAplicacao(ResultadoDaImportacao.ComErros, Plano: plano);
                    }

                    var registro = new ImportacaoRegistrada(importacaoId, hash, plano.Resumo, request.UserId, clock.UtcNow);
                    if (!await importacoes.RegistrarAsync(org, registro, ct))
                    {
                        // A concurrent apply of the same id committed first (the insert waited for it): its result is the answer and this unit wrote nothing.
                        return Repetida((await importacoes.AcharAsync(org, importacaoId, ct))!, hash);
                    }

                    await GravarAsync(request, plano, ct);
                    await audit.RecordAsync(request, new AuditEntry { Action = ImportacaoAuditActions.Importar, ObjectType = "cardapio", ObjectId = importacaoId.ToString("D"), After = Contagens(plano.Resumo) }, ct);
                    await eventos.PublicarAsync(request.Organization, new CardapioImportado(EntityId.New().Value, importacaoId, plano.Resumo), ct);
                    return new ResultadoDaAplicacao(ResultadoDaImportacao.Ok, plano.Resumo, Plano: plano);
                },
                cancellationToken);
        }
        catch (PrecoNegadoException)
        {
            return new ResultadoDaAplicacao(ResultadoDaImportacao.Negado);
        }
        catch (ImportacaoDesatualizadaException)
        {
            return new ResultadoDaAplicacao(ResultadoDaImportacao.Desatualizada);
        }
    }

    private static ResultadoDaAplicacao Repetida(ImportacaoRegistrada anterior, string hash) =>
        anterior.Hash == hash
            ? new ResultadoDaAplicacao(ResultadoDaImportacao.JaAplicada, anterior.Resumo)
            : new ResultadoDaAplicacao(ResultadoDaImportacao.IdReutilizado);

    private async Task<EstadoDoCardapio> EstadoAtualAsync(Guid org, Guid unit, CancellationToken ct) => new(
        await cardapio.ListarCategoriasAsync(org, ct),
        await cardapio.ListarProdutosAsync(org, unit, true, ct),
        await opcoes.ListarGruposAsync(org, true, ct),
        await opcoes.VinculosDosProdutosAsync(org, ct));

    /// <summary>Writes the plan in dependency order: categories (parents first), groups with their options, products, then the product links. A stale version (an edit in between) throws, rolling everything back.</summary>
    private async Task GravarAsync(IRequestContext request, PlanoDaImportacao plano, CancellationToken ct)
    {
        var org = request.Organization.OrganizationId;
        var agora = clock.UtcNow;
        foreach (var c in plano.Categorias.Where(c => c.Acao != AcaoDaImportacao.SemMudanca).OrderBy(c => c.Categoria.CategoriaPaiId is null ? 0 : 1))
        {
            if (c.Acao == AcaoDaImportacao.Criar)
            {
                await cardapio.InserirCategoriaAsync(org, c.Categoria, agora, ct);
            }
            else if (!await cardapio.AlterarCategoriaAsync(org, c.Categoria, c.Categoria.Versao - 1, agora, ct))
            {
                throw new ImportacaoDesatualizadaException();
            }
        }

        foreach (var g in plano.Grupos.Where(g => g.Acao != AcaoDaImportacao.SemMudanca))
        {
            if (g.Acao == AcaoDaImportacao.Criar)
            {
                await opcoes.InserirGrupoAsync(org, g.Grupo, agora, ct);
            }
            else if (!await opcoes.AlterarGrupoAsync(org, g.Grupo, g.Grupo.Versao - 1, agora, ct))
            {
                throw new ImportacaoDesatualizadaException();
            }
        }

        foreach (var p in plano.Produtos.Where(p => p.Acao != AcaoDaImportacao.SemMudanca))
        {
            if (p.Acao == AcaoDaImportacao.Criar)
            {
                await cardapio.InserirProdutoAsync(org, p.Produto, agora, ct);
            }
            else
            {
                await AtualizarProdutoAsync(request, p, agora, ct);
            }

            if (p.GrupoIds is not null)
            {
                await opcoes.DefinirGruposDoProdutoAsync(org, p.Produto.Id, p.GrupoIds, ct);
            }
        }
    }

    private async Task AtualizarProdutoAsync(IRequestContext request, ProdutoNoPlano p, DateTimeOffset agora, CancellationToken ct)
    {
        var org = request.Organization.OrganizationId;
        var antes = await cardapio.AcharProdutoAsync(org, request.Organization.UnitId, p.Produto.Id, ct) ?? throw new ImportacaoDesatualizadaException();
        if (p.Produto.Versao == antes.Versao)
        {
            // Only the links change; the product row stays.
            return;
        }

        if (!await cardapio.AlterarProdutoAsync(org, p.Produto, antes.Versao, agora, ct))
        {
            throw new ImportacaoDesatualizadaException();
        }

        if (antes.PrecoCentavos != p.Produto.PrecoCentavos)
        {
            // Measured like a manual edit: against the price in force now, with the unit override and the promotion left as they are.
            var vigenteAntes = antes.PrecoEfetivo(agora);
            var vigenteDepois = (antes with { PrecoCentavos = p.Produto.PrecoCentavos }).PrecoEfetivo(agora);
            var percentual = Math.Max(
                RegrasDePreco.PercentualDaMudanca(antes.PrecoCentavos, p.Produto.PrecoCentavos, antes.PrecoCentavos),
                RegrasDePreco.PercentualDaMudanca(vigenteAntes, vigenteDepois, vigenteAntes));
            var delta = Math.Max(Math.Abs(p.Produto.PrecoCentavos - antes.PrecoCentavos), Math.Abs(vigenteDepois - vigenteAntes));
            if (!await precos.PodeAlterarAsync(request, percentual, delta, ct))
            {
                throw new PrecoNegadoException();
            }
        }
    }

    private static JsonObject Contagens(ResumoDaImportacao r) => new()
    {
        ["categoriasCriadas"] = r.CategoriasCriadas,
        ["categoriasAtualizadas"] = r.CategoriasAtualizadas,
        ["produtosCriados"] = r.ProdutosCriados,
        ["produtosAtualizados"] = r.ProdutosAtualizados,
        ["gruposCriados"] = r.GruposCriados,
        ["gruposAtualizados"] = r.GruposAtualizados,
        ["opcoesCriadas"] = r.OpcoesCriadas,
        ["opcoesAtualizadas"] = r.OpcoesAtualizadas,
    };
}

/// <summary>Signal: the menu changed between reading it and writing the import (a stale version), so the unit of work rolls back and the owner previews again.</summary>
public sealed class ImportacaoDesatualizadaException : Exception
{
    public ImportacaoDesatualizadaException()
        : base("The menu changed during the import.")
    {
    }

    public ImportacaoDesatualizadaException(string message)
        : base(message)
    {
    }

    public ImportacaoDesatualizadaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
