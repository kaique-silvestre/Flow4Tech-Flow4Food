using System.Text.Json.Nodes;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Kernel.Settings;
using Flow4Tech.Organizacao.Domain;

namespace Flow4Tech.Organizacao.Application;

[AuditCatalog]
public static class OrganizacaoAuditActions
{
    /// <summary>A new value, lock or "inherit again" for an option of the organization or of the active unit (spec 5.7.3).</summary>
    public const string ConfiguracaoAlterar = "configuracao:alterar";

    /// <summary>An earlier version put back as a new one (spec 5.7.3).</summary>
    public const string ConfiguracaoRestaurar = "configuracao:restaurar";
}

/// <summary>Port: the stored versions of the current organization. Runs inside a unit of work (the organization context comes from it, RLS does the filtering).</summary>
public interface IConfiguracoesStore
{
    /// <summary>For each (scope, key) the version in force at <paramref name="at"/>: the organization scope and, when given, that one unit. The only operation that also works OUTSIDE a unit of work (it reads in a short transaction of its own).</summary>
    Task<IReadOnlyList<VersaoDeConfiguracao>> EmVigorAsync(Guid organizationId, IReadOnlyCollection<string> chaves, Guid? unitId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Every version of one scope and key, newest first.</summary>
    Task<IReadOnlyList<VersaoDeConfiguracao>> HistoricoAsync(Guid organizationId, string chave, Guid? unitId, CancellationToken cancellationToken);

    /// <summary>The next version number of a scope and key. Takes a transaction lock on the scope, so two writers of the same option are serialized until commit.</summary>
    Task<int> ProximaVersaoAsync(Guid organizationId, string chave, Guid? unitId, CancellationToken cancellationToken);

    Task InserirAsync(Guid organizationId, VersaoDeConfiguracao versao, CancellationToken cancellationToken);
}

/// <summary>Port: may the signed-in person do <paramref name="permission"/>? Adapted by the host over the one authorization engine; this module never reads a profile.</summary>
public interface IPermissaoDoChamador
{
    Task<bool> PodeAsync(IRequestContext request, string permission, CancellationToken cancellationToken);
}

/// <summary>The options the platform knows (collected from the modules' <c>[SettingsCatalog]</c> classes).</summary>
public sealed class CatalogoDeConfiguracoes(IReadOnlyList<SettingDefinition> definicoes)
{
    private readonly Dictionary<string, SettingDefinition> _porChave = definicoes.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public IReadOnlyList<SettingDefinition> Todas { get; } = definicoes;

    public SettingDefinition? Achar(string? chave) => chave is not null && _porChave.TryGetValue(chave, out var d) ? d : null;
}

/// <summary>
/// The real <see cref="ISettings"/>: the system layer of <see cref="SystemSettings"/> plus what the organization and its units stored. Reads join the unit of work in progress
/// (one connection, one snapshot) or open a short one; nothing is cached, so a version that starts to apply is read on the very next call.
/// </summary>
public sealed class ResolvedorDeConfiguracoes(IConfiguracoesStore store, Func<SettingDefinition, string?> configurado) : SystemSettings(configurado)
{
    public override async Task<ResolvedSetting> ResolveAsync(SettingDefinition definition, Guid organizationId, Guid? unitId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var sistema = SystemValue(definition);
        if (!definition.AllowsOrganization)
        {
            return new ResolvedSetting(definition, sistema, SettingLayer.Sistema, false);
        }

        var unit = definition.AllowsUnit && unitId is { } id && id != Guid.Empty ? id : (Guid?)null;
        var versoes = await store.EmVigorAsync(organizationId, [definition.Key], unit, at, cancellationToken);
        return ResolvedorDeConfiguracao.Resolver(definition, sistema, versoes, at, unit);
    }
}

public enum ResultadoConfiguracao
{
    Ok,

    /// <summary>The caller lacks <c>organizacao:configurar</c> (403, no reason to the client).</summary>
    Negado,

    ChaveDesconhecida,

    /// <summary>The option does not apply on that layer (a system-only option, or the unit layer of an organization-only one).</summary>
    CamadaInvalida,

    ValorInvalido,

    /// <summary>The organization locked the option: the unit may not override it.</summary>
    Travada,

    /// <summary>The instant is in the past, or an option that needs an explicit instant did not get one (a change never reaches back).</summary>
    VigenciaInvalida,

    /// <summary>The unit layer was asked but the session has no active unit.</summary>
    SemUnidadeAtiva,

    /// <summary>The version to restore does not exist.</summary>
    VersaoInexistente,
}

public enum CamadaDeEscrita
{
    Organizacao,
    Unidade,
}

public sealed record AlterarConfiguracao(string Chave, CamadaDeEscrita Camada, string? Valor, bool? Travado, DateTimeOffset? VigenteAPartirDe, string? Motivo);

public sealed record ConfiguracaoEmVigor(SettingDefinition Definicao, string Valor, SettingLayer Origem, bool TravadaPelaOrganizacao, string? ValorDaOrganizacao, string? ValorDaUnidade, string Padrao);

public sealed record LinhaDoHistorico(VersaoDeConfiguracao Versao, bool EmVigor, bool Agendada, bool Substituida);

/// <summary>
/// Reading and changing the layered settings (spec 5.7). Every change is ONE unit of work: ask the engine for <c>organizacao:configurar</c>, validate, append a version (never an
/// update), audit it. The organization and the unit always come from the session: the unit layer is the session's ACTIVE unit. Nothing here reads a profile name.
/// </summary>
public sealed class Configuracoes(IUnitOfWork unitOfWork, IConfiguracoesStore store, CatalogoDeConfiguracoes catalogo, ISettings settings, IPermissaoDoChamador permissoes, IAuditLog audit, IClock clock)
{
    /// <summary>Every option with its value for the organization and the active unit now, and the value each layer holds. Needs the same permission as changing them.</summary>
    public async Task<IReadOnlyList<ConfiguracaoEmVigor>?> ListarAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!await permissoes.PodeAsync(request, OrganizacaoPermissions.Configurar, ct))
                {
                    return null;
                }

                var org = request.Organization.OrganizationId;
                var unit = request.Organization.UnitId == Guid.Empty ? (Guid?)null : request.Organization.UnitId;
                var now = clock.UtcNow;
                var versoes = await store.EmVigorAsync(org, [.. catalogo.Todas.Where(d => d.AllowsOrganization).Select(d => d.Key)], unit, now, ct);
                var lista = new List<ConfiguracaoEmVigor>();
                foreach (var d in catalogo.Todas)
                {
                    var sistema = settings.SystemValue(d);
                    var resolvida = ResolvedorDeConfiguracao.Resolver(d, sistema, versoes, now, unit);
                    var daOrganizacao = ResolvedorDeConfiguracao.EmVigor(versoes.Where(v => v.Chave == d.Key && v.UnidadeId is null), now)?.Valor;
                    var daUnidade = unit is null ? null : ResolvedorDeConfiguracao.EmVigor(versoes.Where(v => v.Chave == d.Key && v.UnidadeId == unit), now)?.Valor;
                    lista.Add(new ConfiguracaoEmVigor(d, resolvida.Value, resolvida.Source, resolvida.LockedByOrganization, d.AllowsOrganization ? daOrganizacao : null, d.AllowsUnit ? daUnidade : null, sistema));
                }

                return (IReadOnlyList<ConfiguracaoEmVigor>)lista;
            },
            cancellationToken);
    }

    /// <summary>Who changed what and when, newest first, for one option on one layer. Null when the caller may not configure.</summary>
    public async Task<(ResultadoConfiguracao Resultado, IReadOnlyList<LinhaDoHistorico> Linhas)> HistoricoAsync(IRequestContext request, string chave, CamadaDeEscrita camada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!await permissoes.PodeAsync(request, OrganizacaoPermissions.Configurar, ct))
                {
                    return (ResultadoConfiguracao.Negado, (IReadOnlyList<LinhaDoHistorico>)[]);
                }

                var erro = Escopo(request, chave, camada, out var definicao, out var unit);
                if (erro is not null)
                {
                    return (erro.Value, (IReadOnlyList<LinhaDoHistorico>)[]);
                }

                var historico = await store.HistoricoAsync(request.Organization.OrganizationId, definicao!.Key, unit, ct);
                var now = clock.UtcNow;
                var emVigor = ResolvedorDeConfiguracao.EmVigor(historico, now);
                var linhas = historico
                    .Select(v =>
                    {
                        var vigor = emVigor is not null && v.Versao == emVigor.Versao;
                        // A scheduled version is hidden when a version written LATER starts no later than it does: that one wins whenever this one would (it cancels it).
                        var agendada = v.VigenteAPartirDe > now && !historico.Any(w => w.Versao > v.Versao && w.VigenteAPartirDe <= v.VigenteAPartirDe);
                        return new LinhaDoHistorico(v, vigor, agendada, !vigor && !agendada);
                    })
                    .ToList();
                return (ResultadoConfiguracao.Ok, (IReadOnlyList<LinhaDoHistorico>)linhas);
            },
            cancellationToken);
    }

    /// <summary>
    /// Sets (or, with a null value, goes back to inheriting) an option on the organization or on the active unit. <see cref="AlterarConfiguracao.VigenteAPartirDe"/> null means from now;
    /// an instant before now is refused; an option that changes how operations read their own past (<see cref="SettingDefinition.RequiresEffectiveInstant"/>) must name the instant.
    /// </summary>
    public Task<ResultadoConfiguracao> AlterarAsync(IRequestContext request, AlterarConfiguracao comando, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(comando);
        return EscreverAsync(request, comando.Chave, comando.Camada, restaurar: null, comando, cancellationToken);
    }

    /// <summary>Puts an earlier version back as a NEW version (the history keeps both). Same rules as a change: the instant is now or later, a lock above still wins.</summary>
    public Task<ResultadoConfiguracao> RestaurarAsync(IRequestContext request, string chave, CamadaDeEscrita camada, int versaoDeOrigem, DateTimeOffset? vigenteAPartirDe, string? motivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EscreverAsync(request, chave, camada, versaoDeOrigem, new AlterarConfiguracao(chave, camada, null, null, vigenteAPartirDe, motivo), cancellationToken);
    }

    private async Task<ResultadoConfiguracao> EscreverAsync(IRequestContext request, string chave, CamadaDeEscrita camada, int? restaurar, AlterarConfiguracao comando, CancellationToken cancellationToken)
    {
        var resultado = await unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!await permissoes.PodeAsync(request, OrganizacaoPermissions.Configurar, ct))
                {
                    return ResultadoConfiguracao.Negado;
                }

                var erro = Escopo(request, chave, camada, out var definicao, out var unit);
                if (erro is not null)
                {
                    return erro.Value;
                }

                var now = clock.UtcNow;
                var org = request.Organization.OrganizationId;
                var vigencia = comando.VigenteAPartirDe?.ToUniversalTime() ?? now;
                if (vigencia < now || (definicao!.RequiresEffectiveInstant && comando.VigenteAPartirDe is null))
                {
                    return ResultadoConfiguracao.VigenciaInvalida;
                }

                if (comando.Motivo is { Length: > 500 })
                {
                    return ResultadoConfiguracao.ValorInvalido;
                }

                var proxima = await store.ProximaVersaoAsync(org, definicao.Key, unit, ct); // serializes the writers of this scope and key
                var historico = await store.HistoricoAsync(org, definicao.Key, unit, ct);
                string? valor;
                bool travado;
                if (restaurar is { } origem)
                {
                    var antiga = historico.FirstOrDefault(v => v.Versao == origem);
                    if (antiga is null)
                    {
                        return ResultadoConfiguracao.VersaoInexistente;
                    }

                    valor = antiga.Valor;
                    travado = antiga.Travado;
                }
                else
                {
                    valor = comando.Valor;
                    travado = comando.Travado ?? historico.Count > 0 && historico[0].Travado; // no word on the lock: keep it as it is
                }

                if (valor is not null)
                {
                    valor = SettingValueRules.Normalize(definicao, valor);
                    if (valor is null)
                    {
                        return ResultadoConfiguracao.ValorInvalido;
                    }
                }

                if (unit is not null)
                {
                    if (travado)
                    {
                        return ResultadoConfiguracao.CamadaInvalida; // a lock lives in the organization layer only
                    }

                    // The lock that counts is the one in force when the new version starts to apply.
                    var daOrganizacao = await store.EmVigorAsync(org, [definicao.Key], null, vigencia, ct);
                    if (ResolvedorDeConfiguracao.EmVigor(daOrganizacao.Where(v => v.UnidadeId is null), vigencia)?.Travado == true)
                    {
                        return ResultadoConfiguracao.Travada;
                    }
                }

                var antes = ResolvedorDeConfiguracao.EmVigor(historico, now);
                var nova = new VersaoDeConfiguracao(EntityId.New().Value, definicao.Key, unit, proxima, valor, travado, vigencia, now, request.UserId, comando.Motivo, restaurar);
                await store.InserirAsync(org, nova, ct);
                await audit.RecordAsync(
                    request,
                    new AuditEntry
                    {
                        Action = restaurar is null ? OrganizacaoAuditActions.ConfiguracaoAlterar : OrganizacaoAuditActions.ConfiguracaoRestaurar,
                        ObjectType = "configuracao",
                        ObjectId = definicao.Key,
                        Before = antes is null ? null : Snapshot(antes),
                        After = Snapshot(nova),
                    },
                    ct);
                return ResultadoConfiguracao.Ok;
            },
            cancellationToken);

        return resultado;
    }

    private ResultadoConfiguracao? Escopo(IRequestContext request, string chave, CamadaDeEscrita camada, out SettingDefinition? definicao, out Guid? unit)
    {
        definicao = catalogo.Achar(chave);
        unit = null;
        if (definicao is null)
        {
            return ResultadoConfiguracao.ChaveDesconhecida;
        }

        if (camada == CamadaDeEscrita.Organizacao)
        {
            return definicao.AllowsOrganization ? null : ResultadoConfiguracao.CamadaInvalida;
        }

        if (!definicao.AllowsUnit)
        {
            return ResultadoConfiguracao.CamadaInvalida;
        }

        if (request.Organization.UnitId == Guid.Empty)
        {
            return ResultadoConfiguracao.SemUnidadeAtiva;
        }

        unit = request.Organization.UnitId;
        return null;
    }

    private static JsonObject Snapshot(VersaoDeConfiguracao v) => new()
    {
        ["camada"] = v.UnidadeId is null ? "organizacao" : "unidade",
        ["versao"] = v.Versao,
        ["valor"] = v.Valor,
        ["travado"] = v.Travado,
        ["vigenteAPartirDe"] = v.VigenteAPartirDe.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ["restauraVersao"] = v.RestauraVersao,
    };
}
