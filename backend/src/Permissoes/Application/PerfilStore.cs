using Flow4Tech.Permissoes.Domain;

namespace Flow4Tech.Permissoes.Application;

/// <summary>A custom profile of an organization with the rules it owns (not the ones it inherits from its template).</summary>
public sealed record PerfilPersonalizado(Guid Id, string Nome, string? Modelo, bool AcompanhaModelo, IReadOnlyList<RegraDeAcesso> Regras);

/// <summary>
/// Storage of custom profiles, assignments and individual overrides of the current organization. Every member runs INSIDE a unit of work (RLS does the filtering);
/// nothing here decides who may do what (that is <see cref="GestaoDePerfis"/>). Changes end the sessions of the affected accounts by database triggers (migration 0010).
/// </summary>
public interface IPerfilStore
{
    /// <summary>False when the organization already has a profile with that name; nothing is written then.</summary>
    Task<bool> CriarAsync(Guid organizationId, PerfilPersonalizado novo, DateTimeOffset em, CancellationToken cancellationToken);

    /// <summary>Every custom profile of the organization (RLS) with its own rules, by name.</summary>
    Task<IReadOnlyList<PerfilPersonalizado>> ListarAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<PerfilPersonalizado?> ObterAsync(Guid organizationId, Guid id, CancellationToken cancellationToken);

    Task SubstituirRegrasAsync(Guid organizationId, Guid id, IReadOnlyList<RegraDeAcesso> regras, CancellationToken cancellationToken);

    /// <summary>The custom profiles a person holds.</summary>
    Task<IReadOnlyList<Guid>> PerfisDoUsuarioAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken);

    /// <summary>False when the person already holds it.</summary>
    Task<bool> AtribuirAsync(Guid organizationId, Guid userId, Guid id, CancellationToken cancellationToken);

    /// <summary>False when the person did not hold it.</summary>
    Task<bool> RemoverAtribuicaoAsync(Guid organizationId, Guid userId, Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<RegraDeAcesso>> OverridesAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken);

    Task SubstituirOverridesAsync(Guid organizationId, Guid userId, IReadOnlyList<RegraDeAcesso> regras, CancellationToken cancellationToken);

    /// <summary>True when EVERY unit exists in the organization (RLS: a unit of another organization does not exist here).</summary>
    Task<bool> UnidadesExistemAsync(Guid organizationId, IReadOnlyCollection<Guid> unidades, CancellationToken cancellationToken);

    /// <summary>True when the account exists in the organization (RLS) and is active.</summary>
    Task<bool> ContaAtivaExisteAsync(Guid organizationId, Guid userId, CancellationToken cancellationToken);
}
