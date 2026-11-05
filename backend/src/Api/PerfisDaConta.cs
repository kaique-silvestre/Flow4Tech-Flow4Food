using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;
using Flow4Tech.Permissoes.Application;

namespace Flow4Tech.Api;

/// <summary>
/// Interim source of the profiles of an account (ticket 06 replaces it with the profiles persisted per organization and the individual overrides): the organization owner holds
/// the factory owner profile, any other ACTIVE account the plain account profile, an inactive or missing one nothing. The only place besides the factory policy that names them.
/// </summary>
internal sealed class PerfisDaConta(IUnitOfWork unitOfWork, IContaStore contas) : IPerfisDoUsuario
{
    public Task<IReadOnlyList<string>> ListarAsync(IRequestContext request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var conta = await contas.ObterAsync(request.Organization.OrganizationId, request.UserId, ct);
                return conta is { Ativa: true } ? [conta.Dono ? PerfisDeFabrica.Dono : PerfisDeFabrica.Conta] : (IReadOnlyList<string>)[];
            },
            cancellationToken);
    }
}
