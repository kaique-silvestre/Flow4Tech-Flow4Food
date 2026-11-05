using System.Security.Cryptography;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Organizacao.Application;

namespace Flow4Tech.Api;

/// <summary>
/// Reads the token and session settings from configuration (environment variables) at the moment the service is first needed, after ALL configuration sources are loaded.
/// <list type="bullet">
/// <item><c>Flow4Tech:Tokens:AppKey</c> (required; at least 32 bytes), <c>AppKeyPrevious</c> (still accepted while a rotation drains), <c>ControlKey</c> (reserved for the Control Tower realm),
/// <c>AccessMinutos</c> (default 5), <c>ClockSkewSegundos</c> (default 30).</item>
/// <item><c>Flow4Tech:Sessoes:InatividadeHoras</c> (default 12), <c>AbsolutaDias</c> (default 14), <c>IntervaloDeAtividadeSegundos</c> (default 60), <c>AllowedOrigins</c> (list; default none).</item>
/// </list>
/// Outside Development and Testing a missing <c>AppKey</c> stops the host from starting. In those two environments a missing key makes an EPHEMERAL random key for the process:
/// tokens die with the process and there is no key to leak or to commit. Ticket 10 moves the session clocks to layered settings.
/// </summary>
internal static class SessionConfiguration
{
    public static TokensOptions Tokens(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var appKey = configuration["Flow4Tech:Tokens:AppKey"];
        if (string.IsNullOrEmpty(appKey) && (environment.IsDevelopment() || environment.IsEnvironment("Testing")))
        {
            appKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        }

        return new TokensOptions
        {
            AppKey = appKey,
            AppKeyPrevious = configuration["Flow4Tech:Tokens:AppKeyPrevious"],
            ControlKey = configuration["Flow4Tech:Tokens:ControlKey"],
            AccessLifetime = TimeSpan.FromMinutes(configuration.GetValue("Flow4Tech:Tokens:AccessMinutos", 5)),
            ClockSkew = TimeSpan.FromSeconds(configuration.GetValue("Flow4Tech:Tokens:ClockSkewSegundos", 30)),
        };
    }

    public static LimitesDeLogin LimitesDeLogin(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var padrao = new LimitesDeLogin();
        var limites = padrao with
        {
            FalhasParaBloquearEmail = configuration.GetValue("Flow4Tech:Identidade:Bloqueio:FalhasEmail", padrao.FalhasParaBloquearEmail),
            FalhasParaBloquearOperacional = configuration.GetValue("Flow4Tech:Identidade:Bloqueio:FalhasOperacional", padrao.FalhasParaBloquearOperacional),
            BloqueioBase = TimeSpan.FromSeconds(configuration.GetValue("Flow4Tech:Identidade:Bloqueio:BloqueioBaseSegundos", padrao.BloqueioBase.TotalSeconds)),
            BloqueioMaximo = TimeSpan.FromMinutes(configuration.GetValue("Flow4Tech:Identidade:Bloqueio:BloqueioMaximoMinutos", padrao.BloqueioMaximo.TotalMinutes)),
            FalhasParaBloquearIp = configuration.GetValue("Flow4Tech:Identidade:Bloqueio:FalhasPorIp", padrao.FalhasParaBloquearIp),
            ContasDistintasPorIp = configuration.GetValue("Flow4Tech:Identidade:Bloqueio:ContasPorIp", padrao.ContasDistintasPorIp),
            IpsDistintosPorConta = configuration.GetValue("Flow4Tech:Identidade:Bloqueio:IpsPorConta", padrao.IpsDistintosPorConta),
        };
        limites.Validar();
        return limites;
    }

    public static RelogiosDaSessao Relogios(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new RelogiosDaSessao(
            TimeSpan.FromHours(configuration.GetValue("Flow4Tech:Sessoes:InatividadeHoras", 12.0)),
            TimeSpan.FromDays(configuration.GetValue("Flow4Tech:Sessoes:AbsolutaDias", 14.0)),
            TimeSpan.FromSeconds(configuration.GetValue("Flow4Tech:Sessoes:IntervaloDeAtividadeSegundos", 60)));
    }

    public static AllowedOrigins Origins(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var values = configuration.GetSection("Flow4Tech:Sessoes:AllowedOrigins").Get<string[]>() ?? [];
        return new AllowedOrigins(values.Select(v => v.Trim().TrimEnd('/')).Where(v => v.Length > 0).ToHashSet(StringComparer.Ordinal));
    }
}

/// <summary>Adapts the Organizacao port to the one Identidade needs, so the two modules stay independent: sessions only need to know HOW MANY units an account has and which one.</summary>
internal sealed class UnidadesDaContaAdapter(IUnidadeVinculos vinculos) : IUnidadesDaConta
{
    public async Task<IReadOnlyList<Guid>> ListarAsync(Guid organizacaoId, Guid contaId, CancellationToken cancellationToken) =>
        [.. (await vinculos.ListarDoUsuarioAsync(organizacaoId, contaId, cancellationToken)).Select(u => u.Id)];
}
