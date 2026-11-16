using Flow4Tech.Caixa.Application;
using Flow4Tech.Cardapio.Application;
using Flow4Tech.Comandas.Application;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Organizacao.Application;
using Flow4Tech.Pagamentos.Application;
using Flow4Tech.Permissoes.Application;

namespace Flow4Tech.Api;

/// <summary>
/// The profile keys of an account (ticket 06): the factory profile that follows from the account (the organization owner holds the factory owner profile, any other ACTIVE account
/// the plain account profile), the custom profiles assigned to it, and its private override profile (<see cref="ChavesDePerfil.DoUsuario"/>, whose rules are the individual
/// overrides). An inactive or missing account holds nothing. The only place besides the factory policy that names the factory profiles.
/// </summary>
internal sealed class PerfisDaConta(IUnitOfWork unitOfWork, IContaStore contas, IPerfilStore perfis) : IPerfisDoUsuario
{
    public Task<IReadOnlyList<string>> ListarAsync(OrganizationContext organization, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return unitOfWork.ExecuteAsync(
            organization,
            async ct =>
            {
                var conta = await contas.ObterAsync(organization.OrganizationId, userId, ct);
                if (conta is not { Ativa: true })
                {
                    return (IReadOnlyList<string>)[];
                }

                var keys = new List<string> { conta.Dono ? PerfisDeFabrica.Dono : PerfisDeFabrica.Conta, ChavesDePerfil.DoUsuario(userId) };
                keys.AddRange((await perfis.PerfisDoUsuarioAsync(organization.OrganizationId, userId, ct)).Select(ChavesDePerfil.Personalizado));
                return keys;
            },
            cancellationToken);
    }
}

/// <summary>Adapts the one authorization engine to the question the Organizacao module asks (<c>organizacao:configurar</c>), so that module reads no profile and references no other module.</summary>
internal sealed class PermissaoDoChamador(IAuthorizationEngine engine, IPerfisDoUsuario perfis) : IPermissaoDoChamador
{
    public async Task<bool> PodeAsync(IRequestContext request, string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organization = request.Organization;
        var profiles = await perfis.ListarAsync(organization, request.UserId, cancellationToken);
        var unit = organization.UnitId == Guid.Empty ? (Guid?)null : organization.UnitId;
        var decision = await engine.DecidirAsync(new Subject(request.UserId, profiles), permission, new Scope(organization.OrganizationId, unit), new DecisionContext(), cancellationToken);
        return decision.IsAllowed;
    }
}

/// <summary>Adapts the one authorization engine to the questions the Caixa and Comandas modules ask (<c>caixa:ver-todos</c>, <c>comanda:ver-comandas-de-outros</c>), so those modules read no profile and reference no other module.</summary>
internal sealed class PermissaoDoOperador(IAuthorizationEngine engine, IPerfisDoUsuario perfis) : IPermissaoDoOperador, IPermissaoDaComanda
{
    public async Task<bool> PodeAsync(IRequestContext request, string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organization = request.Organization;
        var profiles = await perfis.ListarAsync(organization, request.UserId, cancellationToken);
        var unit = organization.UnitId == Guid.Empty ? (Guid?)null : organization.UnitId;
        var decision = await engine.DecidirAsync(new Subject(request.UserId, profiles), permission, new Scope(organization.OrganizationId, unit), new DecisionContext(), cancellationToken);
        return decision.IsAllowed;
    }
}

/// <summary>
/// Adapts the one authorization engine to the question the Cardapio module asks for a price change (<c>cardapio:preco-alterar</c>, percent limited). The percent and the value are
/// computed by the module from the stored price; this only carries them to the engine inside the unit of work in progress (a daily cap, if any, is reserved there).
/// </summary>
internal sealed class DecisaoDePrecoDoCardapio(IAuthorizationEngine engine, IPerfisDoUsuario perfis) : IDecisaoDePreco
{
    public async Task<bool> PodeAlterarAsync(IRequestContext request, decimal percentual, long deltaCentavos, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organization = request.Organization;
        var profiles = await perfis.ListarAsync(organization, request.UserId, cancellationToken);
        var unit = organization.UnitId == Guid.Empty ? (Guid?)null : organization.UnitId;
        var decision = await engine.DecidirAsync(new Subject(request.UserId, profiles), CardapioPermissions.AlterarPreco, new Scope(organization.OrganizationId, unit), new DecisionContext(new Money(deltaCentavos, "BRL"), percentual), cancellationToken);
        return decision.IsAllowed;
    }
}

/// <summary>
/// Adapts the one authorization engine to the question Comandas asks for a discount, a courtesy or a gift (<c>comanda:desconto|cortesia|brinde</c>, limited by percent, value and daily cap). The module
/// computes the percent and the value from the stored comanda; this only carries them to the engine inside the unit of work in progress, where a daily cap is reserved.
/// </summary>
internal sealed class DecisaoDeDescontoDaComanda(IAuthorizationEngine engine, IPerfisDoUsuario perfis) : IDecisaoDeDesconto
{
    public async Task<bool> PodeAsync(IRequestContext request, string permission, decimal percentual, long valorCentavos, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organization = request.Organization;
        var profiles = await perfis.ListarAsync(organization, request.UserId, cancellationToken);
        var unit = organization.UnitId == Guid.Empty ? (Guid?)null : organization.UnitId;
        var decision = await engine.DecidirAsync(new Subject(request.UserId, profiles), permission, new Scope(organization.OrganizationId, unit), new DecisionContext(new Money(valorCentavos, "BRL"), percentual), cancellationToken);
        return decision.IsAllowed;
    }
}

/// <summary>
/// Adapts the one authorization engine to the question Pagamentos asks for a refund (<c>pagamento:estornar</c>, limited by value and daily cap). The module measures the cents from the stored payments; this only
/// carries them to the engine inside the unit of work in progress, where a daily cap is reserved.
/// </summary>
internal sealed class DecisaoDeEstornoDoOperador(IAuthorizationEngine engine, IPerfisDoUsuario perfis) : IDecisaoDeEstorno
{
    public async Task<bool> PodeAsync(IRequestContext request, long valorCentavos, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organization = request.Organization;
        var profiles = await perfis.ListarAsync(organization, request.UserId, cancellationToken);
        var unit = organization.UnitId == Guid.Empty ? (Guid?)null : organization.UnitId;
        var decision = await engine.DecidirAsync(new Subject(request.UserId, profiles), PagamentosPermissions.Estornar, new Scope(organization.OrganizationId, unit), new DecisionContext(new Money(valorCentavos, "BRL")), cancellationToken);
        return decision.IsAllowed;
    }
}

/// <summary>
/// Adapts the one authorization engine to the question Caixa asks for a supply, a withdrawal or the approval of a difference (<c>caixa:suprimento|sangria|aprovar-diferenca</c>, limited by amount and daily cap).
/// The module computes the amount from stored data; this only carries it to the engine inside the unit of work in progress, where a daily cap is reserved.
/// </summary>
internal sealed class DecisaoDeCaixaDoOperador(IAuthorizationEngine engine, IPerfisDoUsuario perfis) : IDecisaoDeCaixa
{
    public async Task<bool> PodeAsync(IRequestContext request, string permission, long valorCentavos, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organization = request.Organization;
        var profiles = await perfis.ListarAsync(organization, request.UserId, cancellationToken);
        var unit = organization.UnitId == Guid.Empty ? (Guid?)null : organization.UnitId;
        var decision = await engine.DecidirAsync(new Subject(request.UserId, profiles), permission, new Scope(organization.OrganizationId, unit), new DecisionContext(new Money(valorCentavos, "BRL")), cancellationToken);
        return decision.IsAllowed;
    }
}

/// <summary>Adapts the operating-day resolver (Permissoes) to the port Comandas declares, so display numbers restart with the unit's operating day without Comandas referencing Permissoes.</summary>
internal sealed class DiaOperacionalDaUnidade(IOperatingDayResolver resolver) : IDiaOperacional
{
    public Task<DateOnly> AtualAsync(OrganizationContext organization, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return resolver.CurrentAsync(organization.OrganizationId, organization.UnitId == Guid.Empty ? null : organization.UnitId, cancellationToken);
    }
}

/// <summary>
/// The operating-day resolver of the host: the unit's zone and rollover come from the layered <c>dia-operacional.*</c> options (organization, then unit), default America/Sao_Paulo and 05:00,
/// so a cap day, a display number and the opening agenda all agree on which day it is.
/// </summary>
internal sealed class DiaOperacionalConfigurado(IServiceProvider services, IClock clock) : IOperatingDayResolver
{
    // Resolved on use, not injected: the agenda writes through Configuracoes, which asks the authorization engine, which holds this resolver (a construction cycle otherwise).
    public Task<DateOnly> CurrentAsync(Guid organizationId, Guid? unitId, CancellationToken cancellationToken) =>
        services.GetRequiredService<AgendaDaUnidade>().DiaOperacionalAsync(organizationId, unitId, clock.UtcNow, cancellationToken);
}

/// <summary>Adapts the unit's agenda to the port Comandas declares: launching is refused outside the opening hours, judged by the server clock.</summary>
internal sealed class HorarioDeFuncionamentoDaUnidade(AgendaDaUnidade agenda) : IHorarioDeFuncionamento
{
    public Task<bool> EstaAbertoAsync(OrganizationContext organization, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organization);
        return agenda.EstaAbertoAsync(organization.OrganizationId, organization.UnitId == Guid.Empty ? null : organization.UnitId, at, cancellationToken);
    }
}
