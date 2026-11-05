using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;

namespace Flow4Tech.Permissoes.Application;

/// <summary>The answer of the simulator: whether the caller may use it at all and, if so, exactly what <c>DecidirAsync</c> answered for the person asked about.</summary>
public sealed record ResultadoSimulacao(bool Autorizado, Decision? Decisao);

/// <summary>
/// "What can this person do?" (spec 5.4.6). It does not evaluate anything itself: it asks THE engine (<c>DecidirAsync</c>) with the profile keys of the person asked about, so the
/// answer cannot drift from the real decision. A capped permission would reserve budget when decided; the question runs in its own unit of work that is rolled back at the end, so
/// asking changes nothing (no reservation, no row). Call it OUTSIDE another unit of work: inside one, the rollback would take the caller's work with it.
/// The caller needs <c>perfil:ver</c> or <c>permissao:atribuir</c>.
/// </summary>
/// <summary>Internal signal that ends the simulator's unit of work by rolling it back; it always carries the answer. Never leaves <see cref="SimuladorDePermissoes"/>.</summary>
public sealed class SimulacaoConcluidaException : Exception
{
    public SimulacaoConcluidaException()
        : this(new ResultadoSimulacao(false, null))
    {
    }

    public SimulacaoConcluidaException(string message)
        : base(message)
    {
        Resultado = new ResultadoSimulacao(false, null);
    }

    public SimulacaoConcluidaException(string message, Exception innerException)
        : base(message, innerException)
    {
        Resultado = new ResultadoSimulacao(false, null);
    }

    public SimulacaoConcluidaException(ResultadoSimulacao resultado)
        : base("simulation finished (rolled back on purpose)")
    {
        Resultado = resultado;
    }

    public ResultadoSimulacao Resultado { get; }
}

public sealed class SimuladorDePermissoes(IUnitOfWork unitOfWork, IAuthorizationEngine engine, IPerfisDoUsuario perfisDoUsuario)
{
    public async Task<ResultadoSimulacao> SimularAsync(
        IRequestContext request,
        Guid userId,
        string permission,
        Guid? unitId,
        DecisionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentNullException.ThrowIfNull(context);
        var organization = request.Organization;
        try
        {
            await unitOfWork.ExecuteAsync(
                organization,
                async ct =>
                {
                    var mine = await perfisDoUsuario.ListarAsync(organization, request.UserId, ct);
                    var callerUnit = organization.UnitId == Guid.Empty ? (Guid?)null : organization.UnitId;
                    var caller = new Subject(request.UserId, mine);
                    var callerScope = new Scope(organization.OrganizationId, callerUnit);
                    var mayView = (await engine.DecidirAsync(caller, PerfisPermissions.Ver, callerScope, new DecisionContext(), ct)).IsAllowed
                        || (await engine.DecidirAsync(caller, PerfisPermissions.Atribuir, callerScope, new DecisionContext(), ct)).IsAllowed;
                    if (!mayView)
                    {
                        throw new SimulacaoConcluidaException(new ResultadoSimulacao(false, null));
                    }

                    var theirs = await perfisDoUsuario.ListarAsync(organization, userId, ct);
                    var decision = await engine.DecidirAsync(new Subject(userId, theirs), permission, new Scope(organization.OrganizationId, unitId), context, ct);
                    throw new SimulacaoConcluidaException(new ResultadoSimulacao(true, decision));
                },
                cancellationToken);
        }
        catch (SimulacaoConcluidaException done)
        {
            return done.Resultado;
        }

        throw new InvalidOperationException("The simulation must end by rolling back.");
    }
}
