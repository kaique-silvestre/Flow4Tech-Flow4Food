using Flow4Tech.Caixa.Application;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Permissoes.Application;

namespace Flow4Tech.Api;

/// <summary>What the caller may do in the salão, so the app can HIDE what it would refuse (an absent action, not a disabled one). It only reads: every action is still decided by the server on its own route.</summary>
internal sealed record PermissoesDoUsuarioResponse(List<string> Permissoes);

internal static class PermissoesDoUsuarioEndpoints
{
    /// <summary>The permissions of the salão slice. Others (settings, accounts) are not offered to the phone app.</summary>
    private static readonly string[] Prefixos = ["comanda:", "caixa:", "mesa:", "produto:", "pagamento:"];

    /// <summary>The profile permissions the phone users screen needs, by exact name (the other <c>perfil:</c>/<c>permissao:</c> entries stay hidden).</summary>
    private static readonly string[] Exatas = [PerfisPermissions.Ver, PerfisPermissions.Gerir, PerfisPermissions.Atribuir];

    public static void MapPermissoesDoUsuario(this IEndpointRouteBuilder app, IEnumerable<string> catalog)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(catalog);
        var oferecidas = catalog.Where(p => Exatas.Contains(p) || Prefixos.Any(prefixo => p.StartsWith(prefixo, StringComparison.Ordinal))).Order(StringComparer.Ordinal).ToList();

        app.MapGet("/sessao/permissoes", async (RequestContextAccessor request, IPermissaoDoOperador permissoes, CancellationToken cancellationToken) =>
            {
                var permitidas = new List<string>();
                foreach (var permissao in oferecidas)
                {
                    if (await permissoes.PodeAsync(request, permissao, cancellationToken))
                    {
                        permitidas.Add(permissao);
                    }
                }

                return Results.Ok(new PermissoesDoUsuarioResponse(permitidas));
            })
            .WithName("getPermissoesDoUsuario")
            .RequireSession()
            .RequirePermission(SessaoPermissions.VerProprias)
            .Produces<PermissoesDoUsuarioResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }
}
