using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Organizacao.Domain;

/// <summary>
/// One stored version of an option at one scope (the organization, or one unit of it): the history is a sequence of these and nothing is ever updated or deleted (spec 5.7.3).
/// <see cref="Valor"/> null means "inherit from the layer above". <see cref="Travado"/> (organization scope only) stops the units from overriding. The version applies from
/// <see cref="VigenteAPartirDe"/>, which is never before <see cref="CriadoEm"/>: a change does not reach back (spec 5.7.4). <see cref="RestauraVersao"/> names the version a restore copied.
/// </summary>
public sealed record VersaoDeConfiguracao(
    Guid Id,
    string Chave,
    Guid? UnidadeId,
    int Versao,
    string? Valor,
    bool Travado,
    DateTimeOffset VigenteAPartirDe,
    DateTimeOffset CriadoEm,
    Guid? CriadoPor,
    string? Motivo,
    int? RestauraVersao);

/// <summary>The resolution of an option from its layers (spec 5.7.1). Pure.</summary>
public static class ResolvedorDeConfiguracao
{
    /// <summary>
    /// The value at <paramref name="at"/>. At each scope the effective version is the LATEST created among those already in force at <paramref name="at"/> (so a version written later
    /// replaces one that was scheduled but has not started). A lock in the organization's effective version makes the unit's version inert. Versions of layers the option does not allow are ignored,
    /// and so are versions of units other than <paramref name="unitId"/>.
    /// </summary>
    public static ResolvedSetting Resolver(SettingDefinition definition, string systemValue, IEnumerable<VersaoDeConfiguracao> versoes, DateTimeOffset at, Guid? unitId = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(systemValue);
        ArgumentNullException.ThrowIfNull(versoes);
        var emVigor = versoes.Where(v => v.Chave == definition.Key && v.VigenteAPartirDe <= at).ToList();
        var organizacao = definition.AllowsOrganization ? Ultima(emVigor.Where(v => v.UnidadeId is null)) : null;
        var travada = organizacao?.Travado == true;
        var unidade = definition.AllowsUnit && !travada && unitId is { } id ? Ultima(emVigor.Where(v => v.UnidadeId == id)) : null;

        if (unidade?.Valor is { } valorDaUnidade)
        {
            return new ResolvedSetting(definition, valorDaUnidade, SettingLayer.Unidade, travada);
        }

        return organizacao?.Valor is { } valorDaOrganizacao
            ? new ResolvedSetting(definition, valorDaOrganizacao, SettingLayer.Organizacao, travada)
            : new ResolvedSetting(definition, systemValue, SettingLayer.Sistema, travada);
    }

    /// <summary>The effective version of one scope at an instant (the one the history marks as "in force").</summary>
    public static VersaoDeConfiguracao? EmVigor(IEnumerable<VersaoDeConfiguracao> doEscopo, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(doEscopo);
        return Ultima(doEscopo.Where(v => v.VigenteAPartirDe <= at));
    }

    private static VersaoDeConfiguracao? Ultima(IEnumerable<VersaoDeConfiguracao> versoes) => versoes.MaxBy(v => v.Versao);
}
