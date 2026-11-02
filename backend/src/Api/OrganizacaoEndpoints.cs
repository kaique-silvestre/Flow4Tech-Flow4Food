using Flow4Tech.Kernel;
using Flow4Tech.Organizacao.Application;

namespace Flow4Tech.Api;

internal sealed record UnidadeResponse(Guid Id, string Nome);

/// <summary>Switch request. <c>UnidadeDestinoId</c> is the unit the user WANTS to move to (validated against their links); it is not the request context, which always comes from the session.</summary>
internal sealed record TrocarUnidadeRequest(Guid UnidadeDestinoId);

internal sealed record UnidadeAtivaResponse(Guid UnidadeId);

internal static class OrganizacaoEndpoints
{
    public static void MapOrganizacao(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/unidades", async (RequestContextAccessor request, UnidadesDoUsuario unidades, CancellationToken cancellationToken) =>
            {
                var lista = await unidades.ListarAsync(request, cancellationToken);
                return TypedResults.Ok(lista.Select(u => new UnidadeResponse(u.Id, u.Nome)).ToList());
            })
            .WithName("listUnidades")
            .RequireSession(allowWithoutUnit: true)
            .RequirePermission(OrganizacaoPermissions.VerUnidades)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/sessao/unidade-ativa", (RequestContextAccessor request) =>
                TypedResults.Ok(new UnidadeAtivaResponse(request.Organization.UnitId)))
            .WithName("getUnidadeAtiva")
            .RequireSession()
            .RequirePermission(OrganizacaoPermissions.VerUnidades)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/sessao/unidade-ativa", async (TrocarUnidadeRequest body, RequestContextAccessor request, UnidadesDoUsuario unidades, ISessionSource sessions, CancellationToken cancellationToken) =>
            {
                // Check, audit record and the session change share one unit of work; a refusal is recorded on its own transaction (spec 5.1.4, 5.6.4).
                var trocou = await unidades.TrocarAsync(
                    request,
                    body.UnidadeDestinoId,
                    ct => sessions.SetActiveUnitAsync(request.Session, body.UnidadeDestinoId, ct),
                    cancellationToken);
                if (!trocou)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                return Results.Ok(new UnidadeAtivaResponse(body.UnidadeDestinoId));
            })
            .WithName("trocarUnidadeAtiva")
            .RequireSession(allowWithoutUnit: true)
            .WithBodyLimit(1024)
            .RequirePermission(OrganizacaoPermissions.TrocarUnidade)
            .Produces<UnidadeAtivaResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }
}
