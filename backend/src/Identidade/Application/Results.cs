using Flow4Tech.Identidade.Domain;

namespace Flow4Tech.Identidade.Application;

public enum StatusCriacao
{
    Criada,
    IdentificadorInvalido,
    Indisponivel,
    SenhaInvalida,
    SenhaVazada,
    Negada,
}

public sealed record ResultadoCriacaoDono(StatusCriacao Status, Guid? ContaId, IReadOnlyList<ProblemaDeSenha> Problemas);

public sealed record ResultadoCriacaoOperacional(StatusCriacao Status, Guid? ContaId, CredencialTemporaria? Credencial);

public sealed record ResultadoProtecao(bool Permitido, RecusaDeProtecao? Recusa);

public enum StatusTrocaDeSenha
{
    Trocada,
    SenhaAtualInvalida,
    SenhaInvalida,
    SenhaVazada,
    ContaInativa,
}

public sealed record ResultadoTrocaDeSenha(StatusTrocaDeSenha Status, IReadOnlyList<ProblemaDeSenha> Problemas);

/// <summary>
/// Outcome of a login attempt. There is exactly ONE failure and it carries nothing: unknown organization, unknown user, unknown e-mail, wrong password and a
/// deactivated account are indistinguishable (spec 5.2.8). Closed hierarchy: only this assembly can add a case.
/// </summary>
public abstract record ResultadoAutenticacao
{
    private protected ResultadoAutenticacao()
    {
    }
}

public sealed record Autenticado(Guid ContaId, Guid OrganizacaoId, TipoConta Tipo, int VersaoCredencial) : ResultadoAutenticacao;

/// <summary>Right password, but a temporary one: nothing is allowed except changing the password (spec 5.2.1).</summary>
public sealed record DeveTrocarSenha(Guid ContaId, Guid OrganizacaoId, TipoConta Tipo, int VersaoCredencial) : ResultadoAutenticacao;

public sealed record FalhaDeAutenticacao : ResultadoAutenticacao;
