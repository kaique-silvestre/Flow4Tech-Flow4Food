using Flow4Tech.Kernel.Settings;
using Flow4Tech.Organizacao.Application;

namespace Flow4Tech.Api;

internal sealed record ConfiguracaoItem(
    string Chave,
    string Tipo,
    string Descricao,
    string[] Camadas,
    bool ExigeVigencia,
    string Padrao,
    string Valor,
    string Origem,
    bool TravadaPelaOrganizacao,
    string? ValorDaOrganizacao,
    string? ValorDaUnidade);

/// <summary>
/// Change of an option. <c>Camada</c> is where the value is written: <c>organizacao</c>, or <c>unidade</c> for the unit ACTIVE in the session (the unit is never named by the client).
/// <c>Valor</c> null = inherit again. <c>Travado</c> (organization layer): the units may not override; omitted = keep as it is. <c>VigenteAPartirDe</c> omitted = from now; never before now.
/// </summary>
internal sealed record AlterarConfiguracaoRequest(string? Camada, string? Valor, bool? Travado, DateTimeOffset? VigenteAPartirDe, string? Motivo);

internal sealed record RestaurarConfiguracaoRequest(string? Camada, int Versao, DateTimeOffset? VigenteAPartirDe, string? Motivo);

internal sealed record VersaoDaConfiguracao(int Versao, string? Valor, bool Travado, DateTimeOffset VigenteAPartirDe, DateTimeOffset CriadoEm, Guid? CriadoPor, string? Motivo, int? RestauraVersao, bool EmVigor, bool Agendada, bool Substituida);

internal sealed record ConfiguracaoErro(string Codigo);

internal static class ConfiguracaoEndpoints
{
    private const long Body = 4096;

    public static void MapConfiguracao(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/configuracoes", async (RequestContextAccessor request, Configuracoes configuracoes, CancellationToken cancellationToken) =>
            {
                var lista = await configuracoes.ListarAsync(request, cancellationToken);
                return lista is null
                    ? Results.StatusCode(StatusCodes.Status403Forbidden)
                    : Results.Ok(lista.Select(c => new ConfiguracaoItem(
                        c.Definicao.Key,
                        c.Definicao.Type.ToString(),
                        c.Definicao.Description,
                        Camadas(c.Definicao),
                        c.Definicao.RequiresEffectiveInstant,
                        c.Padrao,
                        c.Valor,
                        Origem(c.Origem),
                        c.TravadaPelaOrganizacao,
                        c.ValorDaOrganizacao,
                        c.ValorDaUnidade)).ToList());
            })
            .WithName("listConfiguracoes")
            .RequireSession()
            .RequirePermission(OrganizacaoPermissions.Configurar)
            .Produces<List<ConfiguracaoItem>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapPut("/configuracoes/{chave}", async (string chave, AlterarConfiguracaoRequest body, RequestContextAccessor request, Configuracoes configuracoes, CancellationToken cancellationToken) =>
            {
                if (!TryCamada(body.Camada, out var camada))
                {
                    return Erro(ResultadoConfiguracao.CamadaInvalida);
                }

                return Erro(await configuracoes.AlterarAsync(request, new AlterarConfiguracao(chave, camada, body.Valor, body.Travado, body.VigenteAPartirDe, body.Motivo), cancellationToken));
            })
            .WithName("alterarConfiguracao")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(OrganizacaoPermissions.Configurar, sensitive: true)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ConfiguracaoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ConfiguracaoErro>(StatusCodes.Status404NotFound)
            .Produces<ConfiguracaoErro>(StatusCodes.Status409Conflict);

        app.MapGet("/configuracoes/{chave}/historico", async (string chave, string? camada, RequestContextAccessor request, Configuracoes configuracoes, CancellationToken cancellationToken) =>
            {
                if (!TryCamada(camada, out var escrita))
                {
                    return Erro(ResultadoConfiguracao.CamadaInvalida);
                }

                var (resultado, linhas) = await configuracoes.HistoricoAsync(request, chave, escrita, cancellationToken);
                return resultado != ResultadoConfiguracao.Ok
                    ? Erro(resultado)
                    : Results.Ok(linhas.Select(l => new VersaoDaConfiguracao(
                        l.Versao.Versao,
                        l.Versao.Valor,
                        l.Versao.Travado,
                        l.Versao.VigenteAPartirDe,
                        l.Versao.CriadoEm,
                        l.Versao.CriadoPor,
                        l.Versao.Motivo,
                        l.Versao.RestauraVersao,
                        l.EmVigor,
                        l.Agendada,
                        l.Substituida)).ToList());
            })
            .WithName("historicoConfiguracao")
            .RequireSession()
            .RequirePermission(OrganizacaoPermissions.Configurar)
            .Produces<List<VersaoDaConfiguracao>>()
            .Produces<ConfiguracaoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ConfiguracaoErro>(StatusCodes.Status404NotFound);

        app.MapPost("/configuracoes/{chave}/restaurar", async (string chave, RestaurarConfiguracaoRequest body, RequestContextAccessor request, Configuracoes configuracoes, CancellationToken cancellationToken) =>
            {
                if (!TryCamada(body.Camada, out var camada))
                {
                    return Erro(ResultadoConfiguracao.CamadaInvalida);
                }

                return Erro(await configuracoes.RestaurarAsync(request, chave, camada, body.Versao, body.VigenteAPartirDe, body.Motivo, cancellationToken));
            })
            .WithName("restaurarConfiguracao")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(OrganizacaoPermissions.Configurar, sensitive: true)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ConfiguracaoErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ConfiguracaoErro>(StatusCodes.Status404NotFound)
            .Produces<ConfiguracaoErro>(StatusCodes.Status409Conflict);
    }

    private static string Origem(SettingLayer layer) => layer switch
    {
        SettingLayer.Organizacao => "organizacao",
        SettingLayer.Unidade => "unidade",
        _ => "sistema",
    };

    private static string[] Camadas(SettingDefinition d) => d.AllowsUnit ? ["sistema", "organizacao", "unidade"] : d.AllowsOrganization ? ["sistema", "organizacao"] : ["sistema"];

    private static bool TryCamada(string? text, out CamadaDeEscrita camada)
    {
        camada = default;
        if (string.Equals(text, "organizacao", StringComparison.Ordinal))
        {
            camada = CamadaDeEscrita.Organizacao;
            return true;
        }

        if (string.Equals(text, "unidade", StringComparison.Ordinal))
        {
            camada = CamadaDeEscrita.Unidade;
            return true;
        }

        return false;
    }

    /// <summary>Denied: 403 with an EMPTY body (no reason). The other refusals name only what is wrong with the request itself, never anything about another organization.</summary>
    private static IResult Erro(ResultadoConfiguracao resultado) => resultado switch
    {
        ResultadoConfiguracao.Ok => Results.NoContent(),
        ResultadoConfiguracao.Negado => Results.StatusCode(StatusCodes.Status403Forbidden),
        ResultadoConfiguracao.ChaveDesconhecida or ResultadoConfiguracao.VersaoInexistente => Results.Json(new ConfiguracaoErro(Codigo(resultado)), statusCode: StatusCodes.Status404NotFound),
        ResultadoConfiguracao.Travada => Results.Json(new ConfiguracaoErro(Codigo(resultado)), statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new ConfiguracaoErro(Codigo(resultado)), statusCode: StatusCodes.Status400BadRequest),
    };

    private static string Codigo(ResultadoConfiguracao resultado) => resultado switch
    {
        ResultadoConfiguracao.ChaveDesconhecida => "chave-desconhecida",
        ResultadoConfiguracao.CamadaInvalida => "camada-invalida",
        ResultadoConfiguracao.ValorInvalido => "valor-invalido",
        ResultadoConfiguracao.Travada => "travada",
        ResultadoConfiguracao.VigenciaInvalida => "vigencia-invalida",
        ResultadoConfiguracao.SemUnidadeAtiva => "sem-unidade-ativa",
        ResultadoConfiguracao.VersaoInexistente => "versao-inexistente",
        _ => "invalido",
    };
}
