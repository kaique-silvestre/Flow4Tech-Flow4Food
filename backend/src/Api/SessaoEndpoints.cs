using Flow4Tech.Identidade.Application;
using Flow4Tech.Kernel;

namespace Flow4Tech.Api;

/// <summary>
/// Login body. <c>OrganizacaoDoLogin</c> is the organization the person TYPES to sign in (operational accounts: organization + user name). It identifies who is logging in and is checked
/// against the credential; it is NOT the request context, which after login comes only from the session (spec 5.1.3, hence the name).
/// </summary>
internal sealed record LoginRequest(Guid? OrganizacaoDoLogin, string? Usuario, string? Email, string? Senha, string? CaptchaResposta = null)
{
    /// <summary>Carries a password: it prints as a placeholder so no log line or exception message can ever show it.</summary>
    public override string ToString() => "[login]";
}

/// <summary>A session as the client needs it. The refresh token is NOT here: it travels only in the HttpOnly cookie.</summary>
internal sealed record SessaoResponse(
    string AccessToken,
    int ExpiraEmSegundos,
    Guid SessaoId,
    Guid? UnidadeAtivaId,
    bool DeveTrocarSenha,
    bool RequerSelecaoDeUnidade,
    DateTimeOffset SessaoExpiraEm)
{
    /// <summary>Carries the access token: it prints as a placeholder.</summary>
    public override string ToString() => "[sessao]";
}

internal sealed record TrocarSenhaRequest(string? SenhaAtual, string? NovaSenha)
{
    public override string ToString() => "[trocar-senha]";
}

/// <summary>Why a password change was refused. Only the signed-in person sees it (their own account), so it can be specific.</summary>
internal sealed record TrocarSenhaErro(string Codigo, IReadOnlyList<string> Problemas);

internal sealed record SessaoItem(Guid Id, Guid ContaId, Guid? UnidadeAtivaId, DateTimeOffset CriadaEm, DateTimeOffset UltimaAtividadeEm, DateTimeOffset ExpiraEm, string? Ip, string? Agente, bool Atual);

internal static class SessaoEndpoints
{
    private const long SmallBody = 1024;
    private const long CredentialBody = 4096;

    public static void MapSessao(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // ---- public routes: the credential is in the body (login) or in the refresh cookie (refresh, logout) ----

        app.MapPost("/login", async (LoginRequest body, HttpContext http, Autenticacao autenticacao, Sessoes sessoes, IClock clock, CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrEmpty(body.Senha))
                {
                    return Results.BadRequest();
                }

                ResultadoAutenticacao resultado;
                var contexto = new ContextoDeLogin(http.Connection.RemoteIpAddress?.ToString(), body.CaptchaResposta);
                if (!string.IsNullOrWhiteSpace(body.Email))
                {
                    resultado = await autenticacao.AutenticarPorEmailAsync(body.Email, body.Senha, contexto, cancellationToken);
                }
                else if (body.OrganizacaoDoLogin is { } organizacao && !string.IsNullOrWhiteSpace(body.Usuario))
                {
                    resultado = await autenticacao.AutenticarOperacionalAsync(organizacao, body.Usuario, body.Senha, contexto, cancellationToken);
                }
                else
                {
                    return Results.BadRequest();
                }

                // Login protection (ticket 08, spec 5.5): answered before anything was checked, so these two tell nothing about the account. Unknown names get them too.
                if (resultado is LoginBloqueado bloqueado)
                {
                    http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((bloqueado.Ate - clock.UtcNow).TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                }

                if (resultado is CaptchaExigido)
                {
                    return Results.StatusCode(StatusCodes.Status428PreconditionRequired);
                }

                var emitida = resultado switch
                {
                    Autenticado a => await sessoes.IniciarAsync(a.ContaId, a.OrganizacaoId, a.VersaoCredencial, false, Cliente(http), cancellationToken),
                    DeveTrocarSenha d => await sessoes.IniciarAsync(d.ContaId, d.OrganizacaoId, d.VersaoCredencial, true, Cliente(http), cancellationToken),
                    _ => null,
                };
                // One failure for everything: unknown organization, user, e-mail, wrong password, deactivated or expired credential (spec 5.2.8).
                return emitida is null ? Results.Unauthorized() : Entregar(http, emitida, clock);
            })
            .WithName("login")
            .RequirePermission(SessaoPermissions.Entrar)
            .RequireCsrfGuard()
            .WithBodyLimit(CredentialBody)
            .Produces<SessaoResponse>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status428PreconditionRequired)
            .Produces(StatusCodes.Status429TooManyRequests);

        app.MapPost("/refresh", async (HttpContext http, Sessoes sessoes, IClock clock, CancellationToken cancellationToken) =>
            {
                var cookie = RefreshCookie.Read(http.Request);
                var resultado = cookie is null
                    ? new ResultadoRenovacao(StatusRenovacao.Invalida, null)
                    : await sessoes.RenovarAsync(cookie, Cliente(http), cancellationToken);
                if (resultado.Sessao is null)
                {
                    // Invalid, expired, revoked or reused: the same answer, and the dead cookie is dropped.
                    RefreshCookie.Clear(http.Response);
                    return Results.Unauthorized();
                }

                return Entregar(http, resultado.Sessao, clock);
            })
            .WithName("refresh")
            .RequirePermission(SessaoPermissions.Renovar)
            .RequireCsrfGuard()
            .WithBodyLimit(SmallBody)
            .Produces<SessaoResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/logout", async (HttpContext http, Sessoes sessoes, CancellationToken cancellationToken) =>
            {
                if (RefreshCookie.Read(http.Request) is { } cookie)
                {
                    await sessoes.EncerrarPorRefreshAsync(cookie, cancellationToken);
                }

                RefreshCookie.Clear(http.Response);
                return Results.NoContent();
            })
            .WithName("logout")
            .RequirePermission(SessaoPermissions.Sair)
            .RequireCsrfGuard()
            .WithBodyLimit(SmallBody)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden);

        // ---- the signed-in person's own session ----

        app.MapPost("/sessao/trocar-senha", async (TrocarSenhaRequest body, RequestContextAccessor request, HttpContext http, TrocaDeSenha troca, Sessoes sessoes, IClock clock, CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrEmpty(body.SenhaAtual) || string.IsNullOrEmpty(body.NovaSenha))
                {
                    return Results.BadRequest();
                }

                // The account is the one of the SESSION, never a value from the body (nobody changes another person's password).
                var organizacao = request.Organization.OrganizationId;
                var resultado = await troca.TrocarAsync(organizacao, request.UserId, body.SenhaAtual, body.NovaSenha, cancellationToken);
                switch (resultado.Status)
                {
                    case StatusTrocaDeSenha.Trocada:
                        var emitida = await sessoes.IniciarAposTrocaDeSenhaAsync(organizacao, request.UserId, Cliente(http), cancellationToken);
                        return emitida is null ? Results.Unauthorized() : Entregar(http, emitida, clock);
                    case StatusTrocaDeSenha.SenhaAtualInvalida:
                        return Results.BadRequest(new TrocarSenhaErro("senha-atual-invalida", []));
                    case StatusTrocaDeSenha.SenhaInvalida:
                        return Results.BadRequest(new TrocarSenhaErro("senha-invalida", [.. resultado.Problemas.Select(p => p.ToString())]));
                    case StatusTrocaDeSenha.SenhaVazada:
                        return Results.BadRequest(new TrocarSenhaErro("senha-vazada", []));
                    case StatusTrocaDeSenha.VerificacaoIndisponivel:
                        return Results.Json(new TrocarSenhaErro("verificacao-indisponivel", []), statusCode: StatusCodes.Status503ServiceUnavailable);
                    default:
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            })
            .WithName("trocarSenha")
            .RequireSession(allowWithoutUnit: true, allowPasswordChangePending: true)
            .RequirePermission(SessaoPermissions.TrocarSenha)
            .WithBodyLimit(CredentialBody)
            .Produces<SessaoResponse>()
            .Produces<TrocarSenhaErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces<TrocarSenhaErro>(StatusCodes.Status503ServiceUnavailable);

        app.MapGet("/sessoes", async (RequestContextAccessor request, Sessoes sessoes, CancellationToken cancellationToken) =>
                TypedResults.Ok((await sessoes.ListarPropriasAsync(request, cancellationToken)).Select(Item).ToList()))
            .WithName("listSessoes")
            .RequireSession()
            .RequirePermission(SessaoPermissions.VerProprias)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/sessoes/todas", async (RequestContextAccessor request, Sessoes sessoes, CancellationToken cancellationToken) =>
            {
                var todas = await sessoes.ListarTodasAsync(request, cancellationToken);
                return todas is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : Results.Ok(todas.Select(Item).ToList());
            })
            .WithName("listSessoesTodas")
            .RequireSession()
            .RequirePermission(SessaoPermissions.VerTodas, sensitive: true)
            .Produces<List<SessaoItem>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPost("/sessoes/{id:guid}/revogar", async (Guid id, RequestContextAccessor request, Sessoes sessoes, CancellationToken cancellationToken) =>
                await sessoes.RevogarAsync(request, id, cancellationToken) == ResultadoRevogacao.Revogada ? Results.NoContent() : Results.NotFound())
            .WithName("revogarSessao")
            .RequireSession()
            .RequirePermission(SessaoPermissions.RevogarPropria)
            .WithBodyLimit(SmallBody)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static SessaoItem Item(SessaoResumo s) => new(s.Id, s.ContaId, s.UnidadeAtivaId, s.CriadaEm, s.UltimaAtividadeEm, s.ExpiraEm, s.Ip, s.Agente, s.Atual);

    /// <summary>Where the request came from; <see cref="Sessoes"/> truncates it. The peer address is used as is: <c>X-Forwarded-For</c> is client-controlled and is NOT read here; behind a proxy the host rewrites the peer address from it only when <c>Flow4Tech:Rede:ProxyConfiavel</c> is on (Program.cs).</summary>
    private static ClienteDaSessao Cliente(HttpContext http) => new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());

    /// <summary>The access token goes in the body (the client keeps it in memory), the refresh token only in the cookie; neither may be cached.</summary>
    private static IResult Entregar(HttpContext http, SessaoEmitida emitida, IClock clock)
    {
        http.Response.Headers.CacheControl = "no-store";
        RefreshCookie.Set(http.Response, emitida.RefreshToken, emitida.SessaoExpiraEm - clock.UtcNow);
        return Results.Ok(new SessaoResponse(
            emitida.AccessToken,
            (int)Math.Round((emitida.AccessExpiraEm - clock.UtcNow).TotalSeconds),
            emitida.SessaoId,
            emitida.UnidadeAtivaId,
            emitida.DeveTrocarSenha,
            emitida.UnidadeAtivaId is null,
            emitida.SessaoExpiraEm));
    }
}
