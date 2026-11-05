using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel;

namespace Flow4Tech.Identidade.Infrastructure;

/// <summary>
/// Login throttling in the memory of the process (spec 5.5.1, 5.5.2). Honest limits: the state is per process, so it is lost on a restart and, with N instances, each keeps its own
/// count (the effective limit is up to N times higher); a shared store behind <see cref="ILimitadorDeLogin"/> is the next step when the API runs on more than one instance. The
/// tables are bounded by <see cref="LimitesDeLogin.MaximoDeRegistros"/>: stale entries are dropped to make room; if the table is still full of live entries a NEW key is simply not
/// tracked (fails open for that key, never an unbounded memory). One lock guards everything: every operation is a few dictionary steps.
/// </summary>
public sealed class LimitadorDeLoginEmMemoria(IClock clock, LimitesDeLogin limites) : ILimitadorDeLogin
{
    private const int ExpoenteMaximo = 20;
    private const int MaximoDeVizinhos = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, Estado> _contas = [];
    private readonly Dictionary<string, Estado> _ips = [];
    private DateTimeOffset _ultimaVarredura = DateTimeOffset.MinValue;

    /// <summary>How many keys are tracked (test hook).</summary>
    internal int Registros
    {
        get
        {
            lock (_gate)
            {
                return Math.Max(_contas.Count, _ips.Count);
            }
        }
    }

    public DecisaoDeEntrada Avaliar(ChaveDeLogin chave, string? ip)
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            var conta = Achar(_contas, chave.Identificador, now);
            var porIp = ip is null ? null : Achar(_ips, ip, now);
            var (ate, motivo) = Mais(conta, porIp);
            if (ate > now)
            {
                return new Bloqueada(ate, motivo);
            }

            return (conta?.Falhas ?? 0) >= limites.FalhasParaCaptchaConta || (porIp?.Falhas ?? 0) >= limites.FalhasParaCaptchaIp ? new ExigeCaptcha() : new Livre();
        }
    }

    public EfeitoDaFalha RegistrarFalha(ChaveDeLogin chave, string? ip)
    {
        lock (_gate)
        {
            var now = clock.UtcNow;
            BloqueioIniciado? daConta = null;
            BloqueioIniciado? doIp = null;

            var conta = Obter(_contas, chave.Identificador, now);
            if (conta is not null)
            {
                Contar(conta, now);
                if (ip is not null)
                {
                    Vizinho(conta, ip, now);
                }

                var limite = chave.Tipo == TipoConta.Email ? limites.FalhasParaBloquearEmail : limites.FalhasParaBloquearOperacional;
                if (conta.Falhas >= limite)
                {
                    daConta = Bloquear(conta, MotivoDeBloqueio.TentativasDaConta, Progressivo(conta.Falhas - limite), now);
                }
                else if (conta.Vizinhos.Count >= limites.IpsDistintosPorConta)
                {
                    daConta = Bloquear(conta, MotivoDeBloqueio.IpsDemaisPorConta, limites.BloqueioDeStuffing, now);
                    conta.Vizinhos.Clear();
                }
            }

            var porIp = ip is null ? null : Obter(_ips, ip, now);
            if (porIp is not null)
            {
                Contar(porIp, now);
                Vizinho(porIp, chave.Identificador, now);
                if (porIp.Falhas >= limites.FalhasParaBloquearIp)
                {
                    doIp = Bloquear(porIp, MotivoDeBloqueio.TentativasDoIp, Progressivo(porIp.Falhas - limites.FalhasParaBloquearIp), now);
                }
                else if (porIp.Vizinhos.Count >= limites.ContasDistintasPorIp)
                {
                    doIp = Bloquear(porIp, MotivoDeBloqueio.ContasDemaisPorIp, limites.BloqueioDeStuffing, now);
                    porIp.Vizinhos.Clear();
                }
            }

            return new EfeitoDaFalha(daConta, doIp);
        }
    }

    public void RegistrarSucesso(ChaveDeLogin chave)
    {
        lock (_gate)
        {
            _ = _contas.Remove(chave.Identificador);
        }
    }

    private TimeSpan Progressivo(int excesso)
    {
        var fator = 1L << Math.Clamp(excesso, 0, ExpoenteMaximo);
        var ticks = limites.BloqueioBase.Ticks * fator;
        return ticks < 0 || ticks > limites.BloqueioMaximo.Ticks ? limites.BloqueioMaximo : TimeSpan.FromTicks(ticks);
    }

    private static BloqueioIniciado Bloquear(Estado estado, MotivoDeBloqueio motivo, TimeSpan duracao, DateTimeOffset now)
    {
        estado.BloqueadoAte = now + duracao;
        estado.Motivo = motivo;
        return new BloqueioIniciado(motivo, estado.BloqueadoAte);
    }

    private static (DateTimeOffset Ate, MotivoDeBloqueio Motivo) Mais(Estado? conta, Estado? ip)
    {
        var a = conta?.BloqueadoAte ?? DateTimeOffset.MinValue;
        var b = ip?.BloqueadoAte ?? DateTimeOffset.MinValue;
        return a >= b ? (a, conta?.Motivo ?? default) : (b, ip?.Motivo ?? default);
    }

    /// <summary>Failures of one key: counted inside the window; a stale, unlocked key starts again from zero.</summary>
    private static void Contar(Estado estado, DateTimeOffset now)
    {
        estado.Falhas++;
        estado.UltimaFalha = now;
    }

    private void Vizinho(Estado estado, string vizinho, DateTimeOffset now)
    {
        if (estado.Vizinhos.Count >= MaximoDeVizinhos)
        {
            foreach (var velho in estado.Vizinhos.Where(v => now - v.Value > limites.JanelaDeFalhas).Select(v => v.Key).ToList())
            {
                _ = estado.Vizinhos.Remove(velho);
            }
        }

        if (estado.Vizinhos.Count < MaximoDeVizinhos || estado.Vizinhos.ContainsKey(vizinho))
        {
            estado.Vizinhos[vizinho] = now;
        }
    }

    /// <summary>The state of a key if it exists, reset when stale (no failure inside the window and not locked).</summary>
    private Estado? Achar(Dictionary<string, Estado> tabela, string chave, DateTimeOffset now)
    {
        if (!tabela.TryGetValue(chave, out var estado))
        {
            return null;
        }

        if (Velho(estado, now))
        {
            _ = tabela.Remove(chave);
            return null;
        }

        return estado;
    }

    /// <summary>The state of a key, created if needed. Null when the table is full of live entries (the key is not tracked).</summary>
    private Estado? Obter(Dictionary<string, Estado> tabela, string chave, DateTimeOffset now)
    {
        var existente = Achar(tabela, chave, now);
        if (existente is not null)
        {
            return existente;
        }

        if (tabela.Count >= limites.MaximoDeRegistros)
        {
            Varrer(now);
            if (tabela.Count >= limites.MaximoDeRegistros)
            {
                return null;
            }
        }

        var novo = new Estado();
        tabela[chave] = novo;
        return novo;
    }

    private bool Velho(Estado estado, DateTimeOffset now) => estado.BloqueadoAte <= now && now - estado.UltimaFalha > limites.JanelaDeFalhas;

    /// <summary>Drops stale entries from both tables, at most once a minute (a full table of live entries must not cost a scan per request).</summary>
    private void Varrer(DateTimeOffset now)
    {
        if (now - _ultimaVarredura < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _ultimaVarredura = now;
        foreach (var tabela in new[] { _contas, _ips })
        {
            foreach (var velho in tabela.Where(e => Velho(e.Value, now)).Select(e => e.Key).ToList())
            {
                _ = tabela.Remove(velho);
            }
        }
    }

    private sealed class Estado
    {
        public int Falhas { get; set; }

        public DateTimeOffset UltimaFalha { get; set; }

        public DateTimeOffset BloqueadoAte { get; set; }

        public MotivoDeBloqueio Motivo { get; set; }

        /// <summary>The other side of the relation, with the last time seen: addresses for an account, accounts for an address (credential stuffing).</summary>
        public Dictionary<string, DateTimeOffset> Vizinhos { get; } = [];
    }
}

/// <summary>No CAPTCHA provider (README premise, open question 4): the adaptive rule is inert until one is registered in its place.</summary>
public sealed class CaptchaNaoConfigurado : ICaptcha
{
    public bool Configurado => false;

    public Task<bool> ValidarAsync(string? resposta, string? ip, CancellationToken cancellationToken) => Task.FromResult(false);
}
