using System.Text.Json.Nodes;
using Flow4Tech.Caixa.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Caixa.Application;

/// <summary>
/// What happens to a shift closed above the tolerance: an approver accepts the difference or counts again. The route has asked for <c>caixa:ver-todos</c>; the LIMITED permission
/// <c>caixa:aprovar-diferenca</c> is asked of the engine here, with the total difference the SERVER computed, AFTER the shift row is locked (the transition out of <c>pendente</c>), so a refusal rolls
/// back the transition and any daily cap reserved (403, no reason, audited as <c>acesso:negar</c> on its own transaction). Separation of duties: whoever closed the shift cannot decide it unless
/// <c>caixa.permitir-autoaprovacao</c> is on. A recount within the tolerance concludes the shift; above it the shift stays pending. Every act is an append-only row plus an audit record in the same unit of work.
/// </summary>
public sealed class GestaoDaDiferenca(IUnitOfWork unitOfWork, ICaixaStore store, IDecisaoDeCaixa decisao, IAuditLog audit, IEventosDoCaixa eventos, ISettings settings, IClock clock)
{
    /// <summary>Shifts of the active unit closed above the tolerance and not decided yet, most recently opened first.</summary>
    public Task<IReadOnlyList<Turno>> PendentesAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(request, ct => store.ListarHistoricoAsync(request.Organization.OrganizationId, request.Organization.UnitId, null, null, null, SituacaoDaConferencia.Pendente, GestaoDoCaixa.LimiteDaLista, ct), cancellationToken);
    }

    public Task<ResultadoDoTurno> AprovarAsync(IRequestContext request, Guid turnoId, string? motivo, CancellationToken cancellationToken = default) =>
        DecidirAsync(request, turnoId, motivo, null, null, cancellationToken);

    /// <summary>Counts the cash again (and any method listed; methods not listed keep their count).</summary>
    public Task<ResultadoDoTurno> RecontarAsync(IRequestContext request, Guid turnoId, string? motivo, long contadoCentavos, IReadOnlyList<PedidoDeContagem>? contagens, CancellationToken cancellationToken = default) =>
        DecidirAsync(request, turnoId, motivo, contadoCentavos, contagens ?? [], cancellationToken);

    private async Task<ResultadoDoTurno> DecidirAsync(IRequestContext request, Guid turnoId, string? motivo, long? recontadoCentavos, IReadOnlyList<PedidoDeContagem>? recontagens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var recontar = recontadoCentavos is not null;
        try
        {
            return await unitOfWork.ExecuteAsync(
                request,
                async ct =>
                {
                    if (!RegrasDoCaixa.TextoValido(motivo, RegrasDoCaixa.MotivoMaximo, out var motivoLimpo) || (recontadoCentavos is { } c && !RegrasDoCaixa.ValorValido(c)))
                    {
                        return new ResultadoDoTurno(ResultadoDoCaixa.Invalido);
                    }

                    var org = request.Organization;
                    if (await store.AcharAsync(org.OrganizationId, turnoId, ct) is not { } turno || turno.UnidadeId != org.UnitId)
                    {
                        return new ResultadoDoTurno(ResultadoDoCaixa.NaoEncontrado);
                    }

                    if (turno.FechadoEm is null || turno.Situacao != SituacaoDaConferencia.Pendente)
                    {
                        return new ResultadoDoTurno(ResultadoDoCaixa.NaoPendente);
                    }

                    var autoaprovacao = (await settings.ResolveAsync(CaixaSettings.PermitirAutoaprovacao, org.OrganizationId, org.UnitId, clock.UtcNow, ct)).AsBoolean();
                    if (!RegrasDoCaixa.PodeAprovar(turno.OperadorId == request.UserId, autoaprovacao))
                    {
                        return new ResultadoDoTurno(ResultadoDoCaixa.AutoaprovacaoNegada);
                    }

                    var novas = recontar ? await ContagensDoTurno.MontarAsync(store, org.OrganizationId, turno.Id, recontagens, ct) : [];
                    if (novas is null)
                    {
                        return new ResultadoDoTurno(ResultadoDoCaixa.Invalido);
                    }

                    var atuais = await store.ContagensAtuaisAsync(org.OrganizationId, turno.Id, ct);
                    var antes = RegrasDoCaixa.DiferencaTotal(turno.DiferencaCentavos!.Value, atuais.Select(a => a.DiferencaCentavos));

                    // The transition takes the row lock: of two approvers racing, one wins and the other finds the shift no longer pending.
                    var transitou = recontar
                        ? await store.RecontarDinheiroAsync(org.OrganizationId, turno.Id, recontadoCentavos!.Value, ct)
                        : await store.AprovarAsync(org.OrganizationId, turno.Id, ct);
                    if (!transitou)
                    {
                        return new ResultadoDoTurno(ResultadoDoCaixa.NaoPendente);
                    }

                    // The question comes after the row lock and measures the SERVER's number: the difference the shift was closed with.
                    if (!await decisao.PodeAsync(request, CaixaPermissions.AprovarDiferenca, antes, ct))
                    {
                        throw new DecisaoDeCaixaNegadaException();
                    }

                    long depois = antes;
                    var situacao = SituacaoDaConferencia.Aprovada;
                    var contagensDepois = atuais;
                    if (recontar)
                    {
                        if (novas.Count > 0)
                        {
                            await store.InserirContagensAsync(org.OrganizationId, turno.Id, await store.ProximaSequenciaAsync(org.OrganizationId, turno.Id, ct), novas, clock.UtcNow, ct);
                        }

                        contagensDepois = await store.ContagensAtuaisAsync(org.OrganizationId, turno.Id, ct);
                        depois = RegrasDoCaixa.DiferencaTotal(recontadoCentavos!.Value - turno.EsperadoCentavos!.Value, contagensDepois.Select(a => a.DiferencaCentavos));
                        var tolerancia = (await settings.ResolveAsync(CaixaSettings.ToleranciaDiferenca, org.OrganizationId, org.UnitId, clock.UtcNow, ct)).AsInteger();
                        situacao = RegrasDoCaixa.Situacao(depois, tolerancia);
                        if (situacao == SituacaoDaConferencia.Concluida)
                        {
                            await store.DefinirSituacaoAsync(org.OrganizationId, turno.Id, situacao, ct);
                        }
                    }

                    var aprovacao = new AprovacaoDeDiferenca(EntityId.New().Value, turno.Id, recontar ? "recontagem" : "aprovacao", request.UserId, motivoLimpo!, antes, depois, clock.UtcNow);
                    await store.InserirAprovacaoAsync(org.OrganizationId, turno.UnidadeId, aprovacao, ct);
                    var json = new JsonObject
                    {
                        ["operadorId"] = turno.OperadorId.ToString("D"),
                        ["diferencaAntesCentavos"] = antes,
                        ["diferencaDepoisCentavos"] = depois,
                        ["motivo"] = aprovacao.Motivo,
                        ["situacao"] = GestaoDoCaixa.Palavra(situacao),
                    };
                    if (recontar)
                    {
                        json["contadoCentavos"] = recontadoCentavos;
                        json["contagens"] = ContagensDoTurno.ComoJson(contagensDepois);
                    }

                    await audit.RecordAsync(request, new AuditEntry { Action = recontar ? CaixaAuditActions.RecontarDiferenca : CaixaAuditActions.AprovarDiferenca, ObjectType = "turno-caixa", ObjectId = turno.Id.ToString("D"), After = json }, ct);
                    if (!recontar)
                    {
                        await eventos.PublicarAsync(org, new CaixaDiferencaAprovada(EntityId.New().Value, turno.Id, turno.UnidadeId, turno.OperadorId, request.UserId, antes, aprovacao.Motivo), ct);
                    }

                    var atualizado = (await store.AcharAsync(org.OrganizationId, turno.Id, ct))!;
                    return new ResultadoDoTurno(ResultadoDoCaixa.Ok, atualizado, contagensDepois);
                },
                cancellationToken);
        }
        catch (DecisaoDeCaixaNegadaException)
        {
            await audit.RecordDetachedAsync(
                request,
                new AuditEntry { Action = PlatformAuditActions.AcessoNegar, Result = AuditResult.Negado, ObjectType = "turno-caixa", ObjectId = turnoId.ToString("D"), After = new JsonObject { ["permissao"] = CaixaPermissions.AprovarDiferenca } },
                cancellationToken);
            return new ResultadoDoTurno(ResultadoDoCaixa.Negado);
        }
    }
}
