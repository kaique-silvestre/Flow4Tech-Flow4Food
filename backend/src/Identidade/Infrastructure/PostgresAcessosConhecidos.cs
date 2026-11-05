using Flow4Tech.Identidade.Application;
using Flow4Tech.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Flow4Tech.Identidade.Infrastructure;

/// <summary>
/// Devices and networks already seen for an account (<c>acessos_conhecidos</c>, migration 0012, tenant table, exact RLS). Joins the caller's unit of work. Plain interpolated SQL, no
/// entity in the model. Per account and kind only the most recent <see cref="Limite"/> are kept, so the table cannot grow without bound.
/// </summary>
public sealed class PostgresAcessosConhecidos : IAcessosConhecidos
{
    private const int Limite = 50;
    private const string Aparelho = "aparelho";
    private const string Rede = "rede";

    public async Task<VisitaRegistrada> RegistrarAsync(Guid organizacaoId, Guid contaId, string? aparelho, string? rede, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        await using var context = new IdentidadeDbContext(TenantUnitOfWork.Require(organizacaoId));

        // Read BEFORE writing: did this account have any history? (A concurrent first login may see none twice: no alert for either, harmless.)
        var antes = await context.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM acessos_conhecidos WHERE conta_id = {contaId}").ToListAsync(cancellationToken);
        var temHistorico = antes.Single() > 0;

        var aparelhoNovo = aparelho is not null && await VerAsync(context, organizacaoId, contaId, Aparelho, aparelho, agora, cancellationToken);
        var redeNova = rede is not null && await VerAsync(context, organizacaoId, contaId, Rede, rede, agora, cancellationToken);
        return new VisitaRegistrada(temHistorico, aparelhoNovo, redeNova);
    }

    /// <summary>True when the value had never been seen (inserted); otherwise refreshes its last-seen instant.</summary>
    private static async Task<bool> VerAsync(IdentidadeDbContext context, Guid organizacaoId, Guid contaId, string tipo, string valor, DateTimeOffset agora, CancellationToken cancellationToken)
    {
        var id = Flow4Tech.Kernel.EntityId.New().Value;
        var inserted = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO acessos_conhecidos (id, organizacao_id, conta_id, tipo, valor, visto_primeiro_em, visto_ultimo_em)
            VALUES ({id}, {organizacaoId}, {contaId}, {tipo}, {valor}, {agora}, {agora})
            ON CONFLICT (organizacao_id, conta_id, tipo, valor) DO NOTHING
            """,
            cancellationToken);
        if (inserted == 1)
        {
            await context.Database.ExecuteSqlAsync(
                $"""
                DELETE FROM acessos_conhecidos
                 WHERE conta_id = {contaId} AND tipo = {tipo}
                   AND id IN (SELECT id FROM acessos_conhecidos WHERE conta_id = {contaId} AND tipo = {tipo} ORDER BY visto_ultimo_em DESC, id DESC OFFSET {Limite})
                """,
                cancellationToken);
            return true;
        }

        await context.Database.ExecuteSqlAsync(
            $"UPDATE acessos_conhecidos SET visto_ultimo_em = {agora} WHERE conta_id = {contaId} AND tipo = {tipo} AND valor = {valor} AND visto_ultimo_em < {agora}",
            cancellationToken);
        return false;
    }
}
