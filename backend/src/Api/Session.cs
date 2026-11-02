using Flow4Tech.Identidade.Application;

namespace Flow4Tech.Api;

/// <summary>What the authentication layer knows about a signed-in request. The organization comes from here and nowhere else (spec 5.1).</summary>
internal sealed record AuthenticatedSession(Guid SessionId, Guid UserId, Guid OrganizationId, Guid? ActiveUnitId, bool PasswordChangePending);

/// <summary>
/// Seam between the request context and the session (ticket 04). Reading returns null when there is no valid session. Switching the active unit is a write on the session row,
/// and it joins the unit of work of the switch.
/// </summary>
internal interface ISessionSource
{
    Task<AuthenticatedSession?> ReadAsync(HttpContext context, CancellationToken cancellationToken);

    Task SetActiveUnitAsync(AuthenticatedSession session, Guid unitId, CancellationToken cancellationToken);
}

/// <summary>
/// The real session source. The access token is read ONLY from <c>Authorization: Bearer ...</c> (never from a cookie, the query string or another header: a token in a URL ends up in
/// logs and a cookie would make every route a CSRF target). Everything else is <see cref="Sessoes.AutenticarAsync"/>: signature, audience, lifetime, then the session row and its
/// account on every request, so a revocation takes effect on the next one.
/// </summary>
internal sealed class BearerSessionSource(Sessoes sessoes) : ISessionSource
{
    private const string Scheme = "Bearer ";

    public async Task<AuthenticatedSession?> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var header = context.Request.Headers.Authorization;
        if (header.Count != 1 || header[0] is not { } value || !value.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return null;
        }

        var authenticated = await sessoes.AutenticarAsync(value[Scheme.Length..].Trim(), cancellationToken);
        return authenticated is null
            ? null
            : new AuthenticatedSession(authenticated.SessaoId, authenticated.ContaId, authenticated.OrganizacaoId, authenticated.UnidadeAtivaId, authenticated.DeveTrocarSenha);
    }

    public Task SetActiveUnitAsync(AuthenticatedSession session, Guid unitId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return sessoes.DefinirUnidadeAtivaAsync(session.OrganizationId, session.SessionId, unitId, cancellationToken);
    }
}
