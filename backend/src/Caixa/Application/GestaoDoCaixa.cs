using System.Text.Json.Nodes;
using Flow4Tech.Caixa.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Settings;

namespace Flow4Tech.Caixa.Application;

public enum ResultadoDoCaixa
{
    Ok,

    Invalido,

    /// <summary>The operator already has an open shift in this unit.</summary>
    JaAberto,

    /// <summary>The operator has no open shift in this unit.</summary>
    SemTurnoAberto,

    /// <summary>The engine refused a limited permission (403 without reason).</summary>
    Negado,

    /// <summary>A withdrawal bigger than what the drawer holds.</summary>
    SaldoInsuficiente,

    /// <summary>The shift does not exist in the organization and unit, or the caller may not see it.</summary>
    NaoEncontrado,

    /// <summary>The shift is not waiting for approval (open, concluded, already approved or decided by someone else meanwhile).</summary>
    NaoPendente,

    /// <summary>The caller closed the shift and self-approval is off.</summary>
    AutoaprovacaoNegada,
}

/// <summary>One counted method of a closing or a recount request (non-cash methods only; the cash count is its own field).</summary>
public sealed record PedidoDeContagem(Guid MetodoId, long ContadoCentavos);

public sealed record ResultadoDoTurno(ResultadoDoCaixa Resultado, Turno? Turno = null, IReadOnlyList<ContagemDoMetodo>? Contagens = null);

public sealed record ResultadoDoMovimento(ResultadoDoCaixa Resultado, MovimentoDoCaixa? Movimento = null);

/// <summary>What a method should add up to, hidden (null) while the closer counts blind.</summary>
public sealed record EsperadoDoMetodo(Guid MetodoId, string MetodoNome, long? EsperadoCentavos);

/// <summary>The open shift as the closer sees it before counting. When <see cref="Cego"/> the expected cash and the per-method expectations are null.</summary>
public sealed record ResumoDoTurno(Turno Turno, bool Cego, long SuprimentosCentavos, long SangriasCentavos, long? EsperadoCentavos, IReadOnlyList<EsperadoDoMetodo> PorMetodo);

public sealed record DetalheDoTurno(Turno Turno, IReadOnlyList<ContagemDoMetodo> Contagens, IReadOnlyList<MovimentoDoCaixa> Movimentos, IReadOnlyList<AprovacaoDeDiferenca> Aprovacoes);

/// <summary>Raised inside the unit of work when the engine refuses a limited caixa permission: everything written (and any daily cap reserved) rolls back and the caller answers <see cref="ResultadoDoCaixa.Negado"/>.</summary>
public sealed class DecisaoDeCaixaNegadaException : InvalidOperationException
{
    public DecisaoDeCaixaNegadaException()
        : base("Decisao negada pelo motor.")
    {
    }

    public DecisaoDeCaixaNegadaException(string message)
        : base(message)
    {
    }

    public DecisaoDeCaixaNegadaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Shared by closing and recounting: validates the non-cash counts of a request and joins them with what the system recorded.</summary>
internal static class ContagensDoTurno
{
    public const int Maximo = 20;

    /// <summary>Null when the request is invalid: too many lines, a repeated method, a value out of range, an unknown method or the cash method.</summary>
    public static async Task<IReadOnlyList<ContagemDoMetodo>?> MontarAsync(ICaixaStore store, Guid organizationId, Guid turnoId, IReadOnlyList<PedidoDeContagem>? pedidos, CancellationToken cancellationToken)
    {
        pedidos ??= [];
        if (pedidos.Count > Maximo || pedidos.Select(p => p.MetodoId).Distinct().Count() != pedidos.Count || pedidos.Any(p => !RegrasDoCaixa.ValorValido(p.ContadoCentavos)))
        {
            return null;
        }

        var metodos = await store.MetodosAsync(organizationId, [.. pedidos.Select(p => p.MetodoId)], cancellationToken);
        if (pedidos.Any(p => !metodos.TryGetValue(p.MetodoId, out var m) || m.Dinheiro))
        {
            return null;
        }

        var recebido = await store.RecebidoPorMetodoAsync(organizationId, turnoId, cancellationToken);
        return [.. pedidos.Select(p => new ContagemDoMetodo(p.MetodoId, metodos[p.MetodoId].Nome, p.ContadoCentavos, recebido.GetValueOrDefault(p.MetodoId))).OrderBy(c => c.MetodoNome, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.MetodoId)];
    }

    public static JsonArray ComoJson(IEnumerable<ContagemDoMetodo> contagens) =>
        new([.. contagens.Select(c => (JsonNode)new JsonObject { ["metodo"] = c.MetodoNome, ["contadoCentavos"] = c.ContadoCentavos, ["esperadoCentavos"] = c.EsperadoCentavos, ["diferencaCentavos"] = c.DiferencaCentavos })]);
}

/// <summary>
/// Opening and closing the operator's shift, and reading it. Who may call is the route's job (<c>caixa:abrir</c>, <c>caixa:fechar</c>, decided by the one engine). Each change is ONE unit of work: state
/// change, audit record and outbox event commit or roll back together. The shift belongs to the session's user and active unit, never to anything the client names. Closing counts the cash and, optionally,
/// each other method; a total difference inside the tolerance concludes alone, above it the shift is closed as pending approval (<see cref="GestaoDaDiferenca"/>). With the blind-count option on, a closer
/// holding <c>caixa:fechar-cego</c> never sees the expected value of the OPEN shift.
/// </summary>
public sealed class GestaoDoCaixa(IUnitOfWork unitOfWork, ICaixaStore store, IAuditLog audit, IEventosDoCaixa eventos, IPermissaoDoOperador permissoes, ISettings settings, IClock clock)
{
    public const int LimiteDaLista = 100;

    /// <summary>The wire name of a situation (<c>concluida</c>, <c>pendente</c>, <c>aprovada</c>).</summary>
    public static string Palavra(SituacaoDaConferencia situacao) => situacao switch
    {
        SituacaoDaConferencia.Pendente => "pendente",
        SituacaoDaConferencia.Aprovada => "aprovada",
        _ => "concluida",
    };

    public Task<ResultadoDoTurno> AbrirAsync(IRequestContext request, long fundoCentavos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!RegrasDoCaixa.ValorValido(fundoCentavos))
                {
                    return new ResultadoDoTurno(ResultadoDoCaixa.Invalido);
                }

                var org = request.Organization;
                var turno = new Turno(EntityId.New().Value, org.UnitId, request.UserId, clock.UtcNow, fundoCentavos, null, null, null, null);
                if (!await store.TentarAbrirAsync(org.OrganizationId, turno, ct))
                {
                    return new ResultadoDoTurno(ResultadoDoCaixa.JaAberto);
                }

                await audit.RecordAsync(request, new AuditEntry { Action = CaixaAuditActions.Abrir, ObjectType = "turno-caixa", ObjectId = turno.Id.ToString("D"), After = new JsonObject { ["fundoCentavos"] = fundoCentavos } }, ct);
                await eventos.PublicarAsync(org, new CaixaAberto(EntityId.New().Value, turno.Id, turno.UnidadeId, turno.OperadorId, fundoCentavos), ct);
                return new ResultadoDoTurno(ResultadoDoCaixa.Ok, turno);
            },
            cancellationToken);
    }

    /// <summary>
    /// Closes the operator's open shift with the cash count and the optional count of other methods. Always closes (a difference never blocks the closing): inside the tolerance it concludes, above it the
    /// shift is left pending approval. The expected cash is summed by the database in the closing statement, after the shift row is locked, so a movement or a payment committed an instant earlier is counted.
    /// </summary>
    public Task<ResultadoDoTurno> FecharAsync(IRequestContext request, long contadoCentavos, IReadOnlyList<PedidoDeContagem>? contagens = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!RegrasDoCaixa.ValorValido(contadoCentavos))
                {
                    return new ResultadoDoTurno(ResultadoDoCaixa.Invalido);
                }

                var org = request.Organization;
                if (await store.AcharAbertoAsync(org.OrganizationId, org.UnitId, request.UserId, ct) is not { } aberto)
                {
                    return new ResultadoDoTurno(ResultadoDoCaixa.SemTurnoAberto);
                }

                if (await ContagensDoTurno.MontarAsync(store, org.OrganizationId, aberto.Id, contagens, ct) is null)
                {
                    return new ResultadoDoTurno(ResultadoDoCaixa.Invalido);
                }

                // The database computes the expected cash (float + cash received + supplies - withdrawals) in the closing statement itself.
                if (!await store.FecharAsync(org.OrganizationId, aberto.Id, clock.UtcNow, contadoCentavos, ct) || await store.AcharAsync(org.OrganizationId, aberto.Id, ct) is not { } fechado)
                {
                    return new ResultadoDoTurno(ResultadoDoCaixa.SemTurnoAberto);
                }

                // Joined with the payments AFTER the closing statement took the shift row lock.
                var porMetodo = (await ContagensDoTurno.MontarAsync(store, org.OrganizationId, aberto.Id, contagens, ct))!;
                if (porMetodo.Count > 0)
                {
                    await store.InserirContagensAsync(org.OrganizationId, aberto.Id, 1, porMetodo, clock.UtcNow, ct);
                }

                var tolerancia = (await settings.ResolveAsync(CaixaSettings.ToleranciaDiferenca, org.OrganizationId, org.UnitId, clock.UtcNow, ct)).AsInteger();
                var esperado = fechado.EsperadoCentavos!.Value;
                var diferenca = fechado.DiferencaCentavos!.Value;
                var total = RegrasDoCaixa.DiferencaTotal(diferenca, porMetodo.Select(c => c.DiferencaCentavos));
                var situacao = RegrasDoCaixa.Situacao(total, tolerancia);
                await store.DefinirSituacaoAsync(org.OrganizationId, aberto.Id, situacao, ct);
                var depois = new JsonObject
                {
                    ["fundoCentavos"] = aberto.FundoCentavos,
                    ["contadoCentavos"] = contadoCentavos,
                    ["recebidoDinheiroCentavos"] = fechado.RecebidoDinheiroCentavos,
                    ["suprimentosCentavos"] = fechado.SuprimentosCentavos,
                    ["sangriasCentavos"] = fechado.SangriasCentavos,
                    ["esperadoCentavos"] = esperado,
                    ["diferencaCentavos"] = diferenca,
                    ["diferencaTotalCentavos"] = total,
                    ["toleranciaCentavos"] = tolerancia,
                    ["situacao"] = Palavra(situacao),
                    ["contagens"] = ContagensDoTurno.ComoJson(porMetodo),
                };
                await audit.RecordAsync(request, new AuditEntry { Action = CaixaAuditActions.Fechar, ObjectType = "turno-caixa", ObjectId = aberto.Id.ToString("D"), After = depois }, ct);
                await eventos.PublicarAsync(org, new CaixaFechado(EntityId.New().Value, aberto.Id, aberto.UnidadeId, aberto.OperadorId, contadoCentavos, esperado, diferenca, Palavra(situacao)), ct);
                return new ResultadoDoTurno(ResultadoDoCaixa.Ok, fechado with { Situacao = situacao }, porMetodo);
            },
            cancellationToken);
    }

    /// <summary>The caller's open shift in the active unit, or null. Blind: the cash received so far is hidden.</summary>
    public Task<Turno?> AtualAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var turno = await store.AcharAbertoAsync(request.Organization.OrganizationId, request.Organization.UnitId, request.UserId, ct);
                return turno is null ? null : Ocultar(turno, request, await CegoAsync(request, ct));
            },
            cancellationToken);
    }

    /// <summary>The open shift before counting: movements so far and, unless blind, the expected cash and the expected value of each method used.</summary>
    public Task<ResumoDoTurno?> ResumoAsync(IRequestContext request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                if (await store.AcharAbertoAsync(org.OrganizationId, org.UnitId, request.UserId, ct) is not { } turno)
                {
                    return null;
                }

                var cego = await CegoAsync(request, ct);
                var (suprimentos, sangrias) = await store.TotaisDosMovimentosAsync(org.OrganizationId, turno.Id, ct);
                var recebido = await store.RecebidoPorMetodoAsync(org.OrganizationId, turno.Id, ct);
                var metodos = await store.MetodosAsync(org.OrganizationId, [.. recebido.Keys], ct);
                var porMetodo = recebido
                    .Where(r => metodos.TryGetValue(r.Key, out var m) && !m.Dinheiro)
                    .Select(r => new EsperadoDoMetodo(r.Key, metodos[r.Key].Nome, cego ? null : r.Value))
                    .OrderBy(m => m.MetodoNome, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                long? esperado = cego ? null : RegrasDoCaixa.Esperado(turno.FundoCentavos, turno.RecebidoDinheiroCentavos, suprimentos, sangrias);
                return new ResumoDoTurno(Ocultar(turno, request, cego), cego, suprimentos, sangrias, esperado, porMetodo);
            },
            cancellationToken);
    }

    /// <summary>Shifts of the active unit. Without <c>caixa:ver-todos</c> only the caller's own.</summary>
    public Task<IReadOnlyList<Turno>> ListarAsync(IRequestContext request, CancellationToken cancellationToken = default) =>
        HistoricoAsync(request, null, null, null, null, cancellationToken);

    /// <summary>
    /// History of the unit's shifts by period (shifts OPENED in [<paramref name="de"/>, <paramref name="ate"/>)), optionally of one operator or one situation. Without <c>caixa:ver-todos</c> only the caller's own,
    /// whatever operator was asked for.
    /// </summary>
    public Task<IReadOnlyList<Turno>> HistoricoAsync(IRequestContext request, Guid? operadorId, DateTimeOffset? de, DateTimeOffset? ate, SituacaoDaConferencia? situacao, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var todos = await permissoes.PodeAsync(request, CaixaPermissions.VerTodos, ct);
                var org = request.Organization;
                var lista = await store.ListarHistoricoAsync(org.OrganizationId, org.UnitId, todos ? operadorId : request.UserId, de, ate, situacao, LimiteDaLista, ct);
                var cego = await CegoAsync(request, ct);
                return (IReadOnlyList<Turno>)[.. lista.Select(t => Ocultar(t, request, cego))];
            },
            cancellationToken);
    }

    /// <summary>One shift with its counts, movements and approvals; null when it is not in the unit or the caller may not see it (not his own and no <c>caixa:ver-todos</c>).</summary>
    public Task<DetalheDoTurno?> DetalheAsync(IRequestContext request, Guid turnoId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization;
                if (await store.AcharAsync(org.OrganizationId, turnoId, ct) is not { } turno || turno.UnidadeId != org.UnitId)
                {
                    return null;
                }

                if (turno.OperadorId != request.UserId && !await permissoes.PodeAsync(request, CaixaPermissions.VerTodos, ct))
                {
                    return null;
                }

                var movimentos = await store.ListarMovimentosAsync(org.OrganizationId, turno.Id, ct);
                var contagens = await store.ContagensAtuaisAsync(org.OrganizationId, turno.Id, ct);
                var aprovacoes = await store.ListarAprovacoesAsync(org.OrganizationId, turno.Id, ct);
                return new DetalheDoTurno(Ocultar(turno, request, await CegoAsync(request, ct)), contagens, movimentos, aprovacoes);
            },
            cancellationToken);
    }

    /// <summary>Blind applies to the caller when the option is on for the unit AND the caller holds <c>caixa:fechar-cego</c>.</summary>
    private async Task<bool> CegoAsync(IRequestContext request, CancellationToken ct)
    {
        var org = request.Organization;
        return (await settings.ResolveAsync(CaixaSettings.ConferenciaCega, org.OrganizationId, org.UnitId, clock.UtcNow, ct)).AsBoolean() && await permissoes.PodeAsync(request, CaixaPermissions.FecharCego, ct);
    }

    /// <summary>The caller's OWN open shift under a blind count: what was received so far would give the expected cash away.</summary>
    private static Turno Ocultar(Turno turno, IRequestContext request, bool cego) =>
        cego && turno.FechadoEm is null && turno.OperadorId == request.UserId ? turno with { RecebidoDinheiroCentavos = 0, EsperadoOculto = true } : turno;
}
