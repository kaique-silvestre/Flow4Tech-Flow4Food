using System.Text.Json.Nodes;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Pagamentos.Domain;

namespace Flow4Tech.Pagamentos.Application;

public enum ResultadoDoMetodo
{
    Ok,

    /// <summary>Empty name, too long, or with control characters.</summary>
    Invalido,

    NaoEncontrado,

    /// <summary>Another method of the organization already has this name (case-insensitive).</summary>
    JaExiste,

    /// <summary>Voucher and credit account cannot be turned on before phase 2B.</summary>
    IndisponivelNaFase,
}

public sealed record ResultadoDoMetodoComDados(ResultadoDoMetodo Resultado, MetodoDoSalao? Metodo = null);

/// <summary>
/// The payment methods of the organization: the factory ones (cash, credit, debit, Pix, meal voucher; voucher and credit account created inactive) are created on first use, the organization renames,
/// deactivates or adds its own. The route has already asked for <c>metodo-pagamento:gerenciar</c> (or <c>pagamento:ver</c> to list). Each write is one unit of work with its audit record. Nothing is deleted.
/// </summary>
public sealed class GestaoDeMetodosDePagamento(IUnitOfWork unitOfWork, IMetodosDePagamentoStore metodos, IAuditLog audit, IClock clock)
{
    /// <summary>Every method of the organization (active or not), in the order of the list.</summary>
    public Task<IReadOnlyList<MetodoDoSalao>> ListarAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync<IReadOnlyList<MetodoDoSalao>>(
            request,
            async ct =>
            {
                await metodos.GarantirDeFabricaAsync(request.Organization.OrganizationId, clock.UtcNow, ct);
                return (await metodos.ListarAsync(request.Organization.OrganizationId, ct)).Select(MetodoDoSalao.De).ToList();
            },
            cancellationToken);
    }

    public Task<ResultadoDoMetodoComDados> CriarAsync(IRequestContext request, string? nome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (RegrasDeMetodo.NomeValido(nome) is not { } limpo)
                {
                    return new ResultadoDoMetodoComDados(ResultadoDoMetodo.Invalido);
                }

                var org = request.Organization.OrganizationId;
                await metodos.GarantirDeFabricaAsync(org, clock.UtcNow, ct);
                var ordem = (await metodos.ListarAsync(org, ct)).Max(m => m.Ordem) + 1;
                var novo = new MetodoDePagamento(EntityId.New().Value, null, limpo, TipoDeMetodo.Outro, true, ordem);
                if (!await metodos.CriarAsync(org, novo, clock.UtcNow, ct))
                {
                    return new ResultadoDoMetodoComDados(ResultadoDoMetodo.JaExiste);
                }

                await audit.RecordAsync(request, new AuditEntry { Action = PagamentosAuditActions.CriarMetodo, ObjectType = "metodo-pagamento", ObjectId = novo.Id.ToString("D"), After = Json(novo) }, ct);
                return new ResultadoDoMetodoComDados(ResultadoDoMetodo.Ok, MetodoDoSalao.De(novo));
            },
            cancellationToken);
    }

    public Task<ResultadoDoMetodoComDados> AlterarAsync(IRequestContext request, Guid metodoId, string? nome, bool ativo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (RegrasDeMetodo.NomeValido(nome) is not { } limpo)
                {
                    return new ResultadoDoMetodoComDados(ResultadoDoMetodo.Invalido);
                }

                var org = request.Organization.OrganizationId;
                await metodos.GarantirDeFabricaAsync(org, clock.UtcNow, ct);
                var antes = await metodos.AcharAsync(org, metodoId, ct);
                if (antes is null)
                {
                    return new ResultadoDoMetodoComDados(ResultadoDoMetodo.NaoEncontrado);
                }

                if (ativo && !antes.Ativo && !RegrasDeMetodo.LiberadoNestaFase(antes.Chave))
                {
                    return new ResultadoDoMetodoComDados(ResultadoDoMetodo.IndisponivelNaFase);
                }

                var (existe, nomeLivre) = await metodos.AlterarAsync(org, metodoId, limpo, ativo, ct);
                if (!existe)
                {
                    return new ResultadoDoMetodoComDados(ResultadoDoMetodo.NaoEncontrado);
                }

                if (!nomeLivre)
                {
                    return new ResultadoDoMetodoComDados(ResultadoDoMetodo.JaExiste);
                }

                var depois = antes with { Nome = limpo, Ativo = ativo };
                await audit.RecordAsync(request, new AuditEntry { Action = PagamentosAuditActions.AlterarMetodo, ObjectType = "metodo-pagamento", ObjectId = metodoId.ToString("D"), Before = Json(antes), After = Json(depois) }, ct);
                return new ResultadoDoMetodoComDados(ResultadoDoMetodo.Ok, MetodoDoSalao.De(depois));
            },
            cancellationToken);
    }

    private static JsonObject Json(MetodoDePagamento m) => new() { ["nome"] = m.Nome, ["tipo"] = m.Tipo == TipoDeMetodo.Dinheiro ? "dinheiro" : "outro", ["ativo"] = m.Ativo };
}
