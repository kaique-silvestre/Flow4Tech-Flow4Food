using Flow4Tech.Kernel.Events;

namespace Flow4Tech.Identidade.Application;

// Security events of spec 5.5.6 (the refresh reuse is in SessoesTipos.cs). Stable names: never rename once published. They carry ids and facts, never a token, a hash or a password.

/// <summary>A login from a device or a network never seen for the account (spec 5.5.5). <see cref="Rede"/> is a network prefix and <see cref="Aparelho"/> the normalized user agent.</summary>
public sealed record LoginNovo(Guid EventId, Guid OrganizacaoId, Guid ContaId, Guid SessaoId, bool AparelhoNovo, bool RedeNova, string? Rede, string? Aparelho, DateTimeOffset DetectadoEm) : IDomainEvent
{
    public const string Type = "seguranca.login-novo";

    public string EventType => Type;
}

/// <summary>An account was locked (too many failures, or credential stuffing). <see cref="Motivo"/> is the name of a <see cref="MotivoDeBloqueio"/>.</summary>
public sealed record ContaBloqueada(Guid EventId, Guid OrganizacaoId, Guid ContaId, string Motivo, DateTimeOffset BloqueadaAte, DateTimeOffset DetectadoEm) : IDomainEvent
{
    public const string Type = "seguranca.conta-bloqueada";

    public string EventType => Type;
}

/// <summary>The person changed their own password (every other session of the account ended).</summary>
public sealed record SenhaTrocada(Guid EventId, Guid OrganizacaoId, Guid ContaId, DateTimeOffset TrocadaEm) : IDomainEvent
{
    public const string Type = "seguranca.senha-trocada";

    public string EventType => Type;
}
