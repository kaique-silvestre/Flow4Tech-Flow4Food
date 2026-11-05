using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;
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
