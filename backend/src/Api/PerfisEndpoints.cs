using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;

namespace Flow4Tech.Api;

/// <summary>A rule as the owner proposes or reads it (<c>UnidadeDaRegraId</c> limits the rule to one unit of the SESSION organization; it is not the request context). <c>Efeito</c> is <c>allow</c> or <c>deny</c>; limits (cents, percent) only on allow. Nothing here names an organization: it is the session's.</summary>
internal sealed record RegraDto(string? Efeito, string? Permissao, Guid? UnidadeDaRegraId, decimal? MaxPercentual, long? MaxValorCentavos, long? TetoDiarioCentavos);

internal sealed record PerfilDto(Guid Id, string Nome, string? Modelo, bool AcompanhaModelo, List<RegraDto> Regras);

internal sealed record ModeloCriadoDto(string Chave, string Nome, bool Criado, Guid? PerfilId);

internal sealed record DefinirOverridesRequest(List<RegraDto>? Regras);

internal sealed record PerfisDaPessoaResponse(List<PerfilDto> Perfis, List<RegraDto> Overrides);

internal sealed record ContaDaListaDto(Guid Id, string Nome, string? Usuario, bool Dono, bool Ativa, List<Guid> UnidadeIds, List<Guid> PerfilIds);

internal sealed record PerfilErro(string Codigo);

internal static class PerfisEndpoints
{
    private const long Body = 64 * 1024;

    public static void MapPerfis(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/perfis/modelos", async (RequestContextAccessor request, GestaoDePerfis perfis, CancellationToken cancellationToken) =>
            {
                var r = await perfis.CriarModelosAsync(request, cancellationToken);
                return r.Resultado == ResultadoGestao.Ok
                    ? Results.Json(r.Modelos.Select(m => new ModeloCriadoDto(m.Chave, m.Nome, m.Criado, m.PerfilId)).ToList(), statusCode: StatusCodes.Status201Created)
                    : Erro(r.Resultado);
            })
            .WithName("criarPerfisModelo")
            .RequireSession()
            .WithBodyLimit(1024)
            .RequirePermission(PerfisPermissions.Gerir)
            .Produces<List<ModeloCriadoDto>>(StatusCodes.Status201Created)
            .Produces<PerfilErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/perfis", async (RequestContextAccessor request, GestaoDePerfis perfis, CancellationToken cancellationToken) =>
            {
                var r = await perfis.ListarAsync(request, cancellationToken);
                return r.Resultado == ResultadoGestao.Ok ? Results.Ok(r.Perfis.Select(Dto).ToList()) : Erro(r.Resultado);
            })
            .WithName("listPerfis")
            .RequireSession()
            .RequirePermission(PerfisPermissions.Ver)
            .Produces<List<PerfilDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/contas", async (RequestContextAccessor request, IUnitOfWork unitOfWork, IContaStore contas, IPerfilStore perfis, CancellationToken cancellationToken) =>
            {
                var organizationId = request.Organization.OrganizationId;
                var lista = await unitOfWork.ExecuteAsync(
                    request,
                    async ct =>
                    {
                        var todas = await contas.ListarAsync(organizationId, ct);
                        var atribuicoes = (await perfis.AtribuicoesAsync(organizationId, ct)).ToLookup(a => a.UsuarioId, a => a.PerfilId);
                        return todas.Select(c => new ContaDaListaDto(c.Id, c.Nome, c.Usuario, c.Dono, c.Ativa, [.. c.Unidades], [.. atribuicoes[c.Id].Order()])).ToList();
                    },
                    cancellationToken);
                return Results.Ok(lista);
            })
            .WithName("listContas")
            .RequireSession()
            .RequirePermission(PerfisPermissions.Ver)
            .Produces<List<ContaDaListaDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/contas/{id:guid}/perfis", async (Guid id, RequestContextAccessor request, GestaoDePerfis perfis, CancellationToken cancellationToken) =>
            {
                var r = await perfis.PerfisDaPessoaAsync(request, id, cancellationToken);
                return r.Resultado == ResultadoGestao.Ok
                    ? Results.Ok(new PerfisDaPessoaResponse([.. r.Perfis.Select(Dto)], [.. r.Overrides.Select(Dto)]))
                    : Erro(r.Resultado);
            })
            .WithName("listPerfisDaConta")
            .RequireSession()
            .RequirePermission(PerfisPermissions.Ver)
            .Produces<PerfisDaPessoaResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<PerfilErro>(StatusCodes.Status404NotFound);

        app.MapPut("/contas/{id:guid}/perfis/{perfilId:guid}", async (Guid id, Guid perfilId, RequestContextAccessor request, GestaoDePerfis perfis, CancellationToken cancellationToken) =>
                Resposta(await perfis.AtribuirAsync(request, id, perfilId, cancellationToken)))
            .WithName("atribuirPerfil")
            .RequireSession()
            .RequirePermission(PerfisPermissions.Atribuir)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<PerfilErro>(StatusCodes.Status404NotFound);

        app.MapDelete("/contas/{id:guid}/perfis/{perfilId:guid}", async (Guid id, Guid perfilId, RequestContextAccessor request, GestaoDePerfis perfis, CancellationToken cancellationToken) =>
                Resposta(await perfis.RemoverAtribuicaoAsync(request, id, perfilId, cancellationToken)))
            .WithName("removerAtribuicaoDePerfil")
            .RequireSession()
            .RequirePermission(PerfisPermissions.Atribuir)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<PerfilErro>(StatusCodes.Status404NotFound);

        app.MapPut("/contas/{id:guid}/overrides", async (Guid id, DefinirOverridesRequest body, RequestContextAccessor request, GestaoDePerfis perfis, CancellationToken cancellationToken) =>
            {
                var regras = Regras(body.Regras);
                return regras is null ? Erro(ResultadoGestao.Invalido) : Resposta(await perfis.DefinirOverridesAsync(request, id, regras, cancellationToken));
            })
            .WithName("definirOverridesDaConta")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(PerfisPermissions.Atribuir)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<PerfilErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<PerfilErro>(StatusCodes.Status404NotFound);
    }

    private static PerfilDto Dto(PerfilPersonalizado p) => new(p.Id, p.Nome, p.Modelo, p.AcompanhaModelo, [.. p.Regras.Select(Dto)]);

    private static RegraDto Dto(RegraDeAcesso r) =>
        new(r.Effect == RuleEffect.Allow ? "allow" : "deny", r.Permission, r.UnitId, r.MaxPercent, r.MaxValueCents, r.DailyCapCents);

    /// <summary>Null when the body is missing or names an effect that is neither allow nor deny; the service validates the rest (catalog, limits, units).</summary>
    private static List<RegraDeAcesso>? Regras(List<RegraDto>? regras)
    {
        if (regras is null)
        {
            return null;
        }

        var result = new List<RegraDeAcesso>(regras.Count);
        foreach (var r in regras)
        {
            var effect = r.Efeito switch
            {
                "allow" => (RuleEffect?)RuleEffect.Allow,
                "deny" => RuleEffect.Deny,
                _ => null,
            };
            if (effect is null || r.Permissao is null)
            {
                return null;
            }

            result.Add(new RegraDeAcesso(effect.Value, r.Permissao, r.UnidadeDaRegraId, r.MaxPercentual, r.MaxValorCentavos, r.TetoDiarioCentavos));
        }

        return result;
    }

    private static IResult Resposta(ResultadoGestao resultado) => resultado == ResultadoGestao.Ok ? Results.NoContent() : Erro(resultado);

    // Refusals about rights (not allowed, own rights, escalation) are 403 with an EMPTY body: the reason is audited, never told.
    private static IResult Erro(ResultadoGestao resultado) => resultado switch
    {
        ResultadoGestao.Negado or ResultadoGestao.AutoPromocao or ResultadoGestao.EscalacaoNegada => Results.StatusCode(StatusCodes.Status403Forbidden),
        ResultadoGestao.NaoEncontrado => Results.Json(new PerfilErro("nao-encontrado"), statusCode: StatusCodes.Status404NotFound),
        _ => Results.Json(new PerfilErro("invalido"), statusCode: StatusCodes.Status400BadRequest),
    };
}
