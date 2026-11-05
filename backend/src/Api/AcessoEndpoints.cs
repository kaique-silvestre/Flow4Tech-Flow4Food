using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;

namespace Flow4Tech.Api;

internal sealed record ConviteRequest(string? Email, string? Nome, Guid? PerfilId, Guid? UnidadeInicialId);

internal sealed record ConviteResponse(Guid Id, DateTimeOffset ExpiraEm, bool EmailEnviado);

/// <summary>Why a request was refused, when the caller is entitled to know (they hold the permission, or the token).</summary>
internal sealed record AcessoErro(string Codigo, IReadOnlyList<string> Problemas);

internal sealed record AceitarConviteRequest(string? Token, string? Senha)
{
    public override string ToString() => "[aceitar-convite]";
}

internal sealed record SolicitarRecuperacaoRequest(string? Email);

internal sealed record RedefinirSenhaRequest(string? Token, string? NovaSenha)
{
    public override string ToString() => "[redefinir-senha]";
}

/// <summary>The new temporary credential, shown ONCE to whoever asked for it.</summary>
internal sealed record CredencialTemporariaResponse(string Credencial, DateTimeOffset ExpiraEm)
{
    public override string ToString() => "[credencial]";
}

internal sealed record VincularGoogleRequest(string? SenhaAtual)
{
    public override string ToString() => "[vincular-google]";
}

internal sealed record IniciarGoogleResponse(string Url);

/// <summary>Routes of ticket 09: invitations, access recovery, credential reset and Google (spec 5.2.1, 5.2.3, 5.2.5, 5.2.6).</summary>
internal static class AcessoEndpoints
{
    private const long SmallBody = 1024;
    private const long CredentialBody = 4096;

    public static void MapAcesso(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // ---- invitations ------------------------------------------------------------------------------------------------------------------------

        app.MapPost("/convites", async (ConviteRequest body, RequestContextAccessor request, Convites convites, CancellationToken cancellationToken) =>
            {
                var resultado = await convites.ConvidarAsync(request, body.Email ?? "", body.Nome ?? "", body.PerfilId, body.UnidadeInicialId, cancellationToken);
                return resultado.Status switch
                {
                    StatusConvite.Criado => Results.Json(new ConviteResponse(resultado.ConviteId!.Value, resultado.ExpiraEm!.Value, resultado.EmailEnviado), statusCode: StatusCodes.Status201Created),
                    StatusConvite.EmailInvalido => Results.BadRequest(new AcessoErro("email-invalido", [])),
                    StatusConvite.NomeInvalido => Results.BadRequest(new AcessoErro("nome-invalido", [])),
                    StatusConvite.PerfilOuUnidadeInvalidos => Results.BadRequest(new AcessoErro("perfil-ou-unidade-invalidos", [])),
                    StatusConvite.JaEhMembro => Results.Conflict(new AcessoErro("ja-e-membro", [])),
                    _ => Results.StatusCode(StatusCodes.Status403Forbidden),
                };
            })
            .WithName("criarConvite")
            .RequireSession()
            .RequirePermission(UsuarioPermissions.Convidar)
            .WithBodyLimit(CredentialBody)
            .Produces<ConviteResponse>(StatusCodes.Status201Created)
            .Produces<AcessoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<AcessoErro>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        app.MapPost("/convites/{id:guid}/revogar", async (Guid id, RequestContextAccessor request, Convites convites, CancellationToken cancellationToken) =>
                await convites.RevogarAsync(request, id, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .WithName("revogarConvite")
            .RequireSession()
            .RequirePermission(UsuarioPermissions.Convidar)
            .WithBodyLimit(SmallBody)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        // Public: the token in the body is the credential. The account is created here, with the password the person chooses; the session comes from the normal login afterwards.
        app.MapPost("/convites/aceitar", async (AceitarConviteRequest body, Convites convites, CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrEmpty(body.Token) || string.IsNullOrEmpty(body.Senha))
                {
                    return Results.BadRequest(new AcessoErro("token-invalido", []));
                }

                var resultado = await convites.AceitarAsync(body.Token, body.Senha, cancellationToken);
                return resultado.Status switch
                {
                    StatusAceite.Aceito => Results.NoContent(),
                    StatusAceite.SenhaInvalida => Results.BadRequest(new AcessoErro("senha-invalida", [.. resultado.Problemas.Select(p => p.ToString())])),
                    StatusAceite.SenhaVazada => Results.BadRequest(new AcessoErro("senha-vazada", [])),
                    StatusAceite.VerificacaoIndisponivel => Results.Json(new AcessoErro("verificacao-indisponivel", []), statusCode: StatusCodes.Status503ServiceUnavailable),
                    StatusAceite.EmailIndisponivel => Results.Conflict(new AcessoErro("email-indisponivel", [])),
                    _ => Results.BadRequest(new AcessoErro("token-invalido", [])),
                };
            })
            .WithName("aceitarConvite")
            .RequirePermission(AcessoPublicoPermissions.AceitarConvite)
            .RequireCsrfGuard()
            .WithBodyLimit(CredentialBody)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<AcessoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<AcessoErro>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces<AcessoErro>(StatusCodes.Status503ServiceUnavailable);

        // ---- recovery ----------------------------------------------------------------------------------------------------------------------------

        // Always 202 with an empty body, whatever happened (account known or not, throttled, mail failed): see RecuperacaoDeAcesso.
        app.MapPost("/recuperacao/solicitar", async (SolicitarRecuperacaoRequest body, HttpContext http, RecuperacaoDeAcesso recuperacao, CancellationToken cancellationToken) =>
            {
                await recuperacao.SolicitarAsync(body.Email, new ContextoDeLogin(http.Connection.RemoteIpAddress?.ToString(), null), cancellationToken);
                return Results.Accepted();
            })
            .WithName("solicitarRecuperacao")
            .RequirePermission(AcessoPublicoPermissions.SolicitarRecuperacao)
            .RequireCsrfGuard()
            .WithBodyLimit(SmallBody)
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status413PayloadTooLarge);

        app.MapPost("/recuperacao/redefinir", async (RedefinirSenhaRequest body, RecuperacaoDeAcesso recuperacao, CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrEmpty(body.Token) || string.IsNullOrEmpty(body.NovaSenha))
                {
                    return Results.BadRequest(new AcessoErro("token-invalido", []));
                }

                var resultado = await recuperacao.RedefinirAsync(body.Token, body.NovaSenha, cancellationToken);
                return resultado.Status switch
                {
                    StatusRecuperacao.Redefinida => Results.NoContent(),
                    StatusRecuperacao.SenhaInvalida => Results.BadRequest(new AcessoErro("senha-invalida", [.. resultado.Problemas.Select(p => p.ToString())])),
                    StatusRecuperacao.SenhaVazada => Results.BadRequest(new AcessoErro("senha-vazada", [])),
                    StatusRecuperacao.VerificacaoIndisponivel => Results.Json(new AcessoErro("verificacao-indisponivel", []), statusCode: StatusCodes.Status503ServiceUnavailable),
                    _ => Results.BadRequest(new AcessoErro("token-invalido", [])),
                };
            })
            .WithName("redefinirSenha")
            .RequirePermission(AcessoPublicoPermissions.RedefinirSenha)
            .RequireCsrfGuard()
            .WithBodyLimit(CredentialBody)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<AcessoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces<AcessoErro>(StatusCodes.Status503ServiceUnavailable);

        // ---- credential reset for operational accounts ---------------------------------------------------------------------------------------------

        app.MapPost("/contas/{id:guid}/redefinir-acesso", async (Guid id, HttpContext http, RequestContextAccessor request, GestaoDeContas contas, CancellationToken cancellationToken) =>
            {
                var resultado = await contas.RedefinirAcessoAsync(request, id, cancellationToken);
                if (resultado.Status != StatusRedefinicao.Redefinida)
                {
                    return resultado.Status switch
                    {
                        StatusRedefinicao.NaoEncontrada => Results.NotFound(),
                        StatusRedefinicao.ContaComEmail => Results.Conflict(new AcessoErro("conta-com-email", [])),
                        _ => Results.StatusCode(StatusCodes.Status403Forbidden),
                    };
                }

                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new CredencialTemporariaResponse(resultado.Credencial!.Valor, resultado.ExpiraEm!.Value));
            })
            .WithName("redefinirAcessoDaConta")
            .RequireSession()
            .RequirePermission(UsuarioPermissions.RedefinirAcesso)
            .WithBodyLimit(SmallBody)
            .Produces<CredencialTemporariaResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<AcessoErro>(StatusCodes.Status409Conflict);

        // ---- Google ---------------------------------------------------------------------------------------------------------------------------------

        // Public, no body: starts the round trip (a navigation). 404 when no provider is configured.
        app.MapGet("/login/google", (HttpContext http, ContasGoogle google) =>
            {
                if (google.IniciarEntrada() is not { } inicio)
                {
                    return Results.NotFound();
                }

                OidcCookie.Set(http.Response, inicio.Selo);
                http.Response.Headers.CacheControl = "no-store";
                return Results.Redirect(inicio.Url.ToString());
            })
            .WithName("entrarComGoogle")
            .RequirePermission(SessaoPermissions.EntrarComGoogle)
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        // The one callback registered at Google, for BOTH sign-in and linking (the signed cookie says which). The answer is always a redirect to the front end: success enters (refresh cookie) or reports the link;
        // anything else goes to the same neutral address. The query (code, state) is never logged: the request line carries the route pattern only.
        app.MapGet("/login/google/retorno", async (string? code, string? state, HttpContext http, ContasGoogle google, Sessoes sessoes, OpcoesDeAcesso opcoes, IClock clock, CancellationToken cancellationToken) =>
            {
                var selo = OidcCookie.Read(http.Request);
                OidcCookie.Clear(http.Response); // one use, whatever happens
                http.Response.Headers.CacheControl = "no-store";
                var resultado = await google.ConcluirAsync(selo, state, code, new ContextoDeLogin(http.Connection.RemoteIpAddress?.ToString(), null), cancellationToken);
                var baseUrl = opcoes.UrlBase.AbsoluteUri.TrimEnd('/');
                switch (resultado)
                {
                    case GoogleEntrou entrou:
                        var emitida = await sessoes.IniciarAsync(entrou.Conta.ContaId, entrou.Conta.OrganizacaoId, entrou.Conta.VersaoCredencial, false, SessaoEndpoints.Cliente(http), cancellationToken);
                        RefreshCookie.Set(http.Response, emitida.RefreshToken, emitida.SessaoExpiraEm - clock.UtcNow);
                        return Results.Redirect(baseUrl + "/entrar?google=ok");
                    case GoogleVinculado:
                        return Results.Redirect(baseUrl + "/conta?google=vinculado");
                    default:
                        return Results.Redirect(baseUrl + "/entrar?google=erro");
                }
            })
            .WithName("retornoDoGoogle")
            .RequirePermission(SessaoPermissions.EntrarComGoogle)
            .Produces(StatusCodes.Status302Found)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/conta/google/vinculo", async (VincularGoogleRequest body, HttpContext http, RequestContextAccessor request, ContasGoogle google, CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrEmpty(body.SenhaAtual))
                {
                    return Results.BadRequest(new AcessoErro("senha-invalida", []));
                }

                var resultado = await google.IniciarVinculoAsync(request, body.SenhaAtual, cancellationToken);
                switch (resultado.Status)
                {
                    case StatusInicioVinculo.Iniciado:
                        OidcCookie.Set(http.Response, resultado.Inicio!.Selo);
                        http.Response.Headers.CacheControl = "no-store";
                        return Results.Ok(new IniciarGoogleResponse(resultado.Inicio.Url.ToString()));
                    case StatusInicioVinculo.SenhaInvalida:
                        return Results.BadRequest(new AcessoErro("senha-invalida", []));
                    case StatusInicioVinculo.Bloqueado:
                        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                    case StatusInicioVinculo.NaoConfigurado:
                        return Results.NotFound();
                    default:
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            })
            .WithName("iniciarVinculoGoogle")
            .RequireSession(allowWithoutUnit: true) // linking concerns the account, not a unit: a person just invited has no unit yet
            .RequirePermission(ContaPermissions.GerirGoogle)
            .WithBodyLimit(CredentialBody)
            .Produces<IniciarGoogleResponse>()
            .Produces<AcessoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status429TooManyRequests);

        app.MapDelete("/conta/google/vinculo", async (RequestContextAccessor request, ContasGoogle google, CancellationToken cancellationToken) =>
                await google.DesvincularAsync(request, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .WithName("desvincularGoogle")
            .RequireSession(allowWithoutUnit: true)
            .RequirePermission(ContaPermissions.GerirGoogle)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
    }
}
