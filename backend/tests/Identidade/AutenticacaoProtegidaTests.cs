using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Events;

namespace Flow4Tech.Identidade.Tests;

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public Task<T> ExecuteAsync<T>(OrganizationContext organization, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default) => work(cancellationToken);
}

internal sealed class RecordingAudit : IAuditLog
{
    public List<(string Action, AuditResult Result, bool Detached)> Entries { get; } = [];

    public Task RecordAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add((entry.Action, entry.Result, false));
        return Task.CompletedTask;
    }

    public Task RecordDetachedAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add((entry.Action, entry.Result, true));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingEvents : IEventosDeSeguranca
{
    public List<IDomainEvent> Published { get; } = [];

    public Task PublicarAsync(OrganizationContext organization, IDomainEvent evento, CancellationToken cancellationToken)
    {
        Published.Add(evento);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingLog : ITechnicalLog
{
    public List<(LogSeverity Severity, string Name, IReadOnlyDictionary<string, object?>? Fields)> Lines { get; } = [];

    public void Write(LogSeverity severity, string eventName, IReadOnlyDictionary<string, object?>? fields = null) => Lines.Add((severity, eventName, fields));
}

/// <summary>"hash:" + password: enough to tell right from wrong, and to count the work.</summary>
internal sealed class FakeHasher : IPasswordHasher
{
    public int Verifications { get; private set; }

    public int DummyVerifications { get; private set; }

    public int Work => Verifications + DummyVerifications;

    public Task<string> HashAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult("hash:" + password);

    public Task<bool> VerifyAsync(string password, string encodedHash, CancellationToken cancellationToken = default)
    {
        Verifications++;
        return Task.FromResult(encodedHash == "hash:" + password);
    }

    public Task VerifyDummyAsync(string password, CancellationToken cancellationToken = default)
    {
        DummyVerifications++;
        return Task.CompletedTask;
    }

    public bool NeedsRehash(string encodedHash) => false;
}

internal sealed class FakeLookup : IPreAuthLookup
{
    private readonly Dictionary<string, ContaParaLogin> _byEmail = [];
    private readonly Dictionary<(Guid, string), ContaParaLogin> _byUser = [];

    public ContaParaLogin AddEmail(string email, Guid org, string senha, bool ativa = true)
    {
        var conta = new ContaParaLogin(Guid.NewGuid(), org, TipoConta.Email, "hash:" + senha, false, ativa, 1, null);
        _byEmail[email] = conta;
        return conta;
    }

    public void AddEmailExistente(string email, ContaParaLogin conta) => _byEmail[email] = conta;

    public ContaParaLogin AddOperacional(Guid org, string usuario, string senha)
    {
        var conta = new ContaParaLogin(Guid.NewGuid(), org, TipoConta.Operacional, "hash:" + senha, false, true, 1, null);
        _byUser[(org, usuario)] = conta;
        return conta;
    }

    public Task<ContaParaLogin?> LocalizarOperacionalAsync(Guid organizacaoId, string usuarioNormalizado, CancellationToken cancellationToken) =>
        Task.FromResult(_byUser.GetValueOrDefault((organizacaoId, usuarioNormalizado)));

    public Task<ContaParaLogin?> LocalizarPorEmailAsync(string emailNormalizado, CancellationToken cancellationToken) =>
        Task.FromResult(_byEmail.GetValueOrDefault(emailNormalizado));
}

internal sealed class FakeCaptcha(bool configurado, bool aceita) : ICaptcha
{
    public bool Configurado { get; } = configurado;

    public List<string?> Recebidos { get; } = [];

    public Task<bool> ValidarAsync(string? resposta, string? ip, CancellationToken cancellationToken)
    {
        Recebidos.Add(resposta);
        return Task.FromResult(aceita && resposta == "resolvido");
    }
}

/// <summary>Spec 5.5.1 to 5.5.3 and 5.5.6 on the login itself, with the real limiter and fakes for everything else (no database).</summary>
public sealed class AutenticacaoProtegidaTests
{
    private static readonly Guid Org = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private const string Certa = "Senha-certa-123";
    private const string Errada = "senha-errada-123";

    private readonly FixedClock _clock = new();
    private readonly FakeLookup _lookup = new();
    private readonly FakeHasher _hasher = new();
    private readonly RecordingAudit _audit = new();
    private readonly RecordingEvents _events = new();
    private readonly RecordingLog _log = new();
    private readonly CountingFloor _floor = new();

    private Autenticacao Criar(LimitesDeLogin? limites = null, ICaptcha? captcha = null) =>
        new(
            _lookup,
            _hasher,
            new FakeUnitOfWork(),
            null!, // the account store is only used to rehash, which the fake hasher never asks for
            _audit,
            _floor,
            _clock,
            new LimitadorDeLoginEmMemoria(_clock, limites ?? new LimitesDeLogin()),
            captcha ?? new CaptchaNaoConfigurado(),
            _events,
            _log);

    private static ContextoDeLogin De(string? ip, string? captcha = null) => new(ip, captcha);

    [Fact]
    public async Task After_the_limit_even_the_right_password_is_refused_and_no_hashing_is_done()
    {
        _lookup.AddEmail("dono@a.com", Org, Certa);
        var auth = Criar(new LimitesDeLogin { FalhasParaBloquearEmail = 3, FalhasParaCaptchaConta = 100 });
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(new FalhaDeAutenticacao(), await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1")));
        }

        var antes = _hasher.Work;
        var resultado = await auth.AutenticarPorEmailAsync("dono@a.com", Certa, De("203.0.113.1"));

        var bloqueado = Assert.IsType<LoginBloqueado>(resultado);
        Assert.Equal(_clock.UtcNow + TimeSpan.FromSeconds(30), bloqueado.Ate);
        Assert.Equal(antes, _hasher.Work); // a locked attempt costs no Argon2 work
        Assert.Equal(4, _floor.Calls); // and still takes the response-time floor
    }

    [Fact]
    public async Task The_lock_ends_and_the_right_password_works_again()
    {
        _lookup.AddEmail("dono@a.com", Org, Certa);
        var auth = Criar(new LimitesDeLogin { FalhasParaBloquearEmail = 2, FalhasParaCaptchaConta = 100 });
        await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1"));
        await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1"));
        Assert.IsType<LoginBloqueado>(await auth.AutenticarPorEmailAsync("dono@a.com", Certa, De("203.0.113.1")));

        _clock.UtcNow += TimeSpan.FromSeconds(31);

        Assert.IsType<Autenticado>(await auth.AutenticarPorEmailAsync("dono@a.com", Certa, De("203.0.113.1")));
    }

    [Fact]
    public async Task A_known_and_an_unknown_account_are_locked_the_same_way()
    {
        _lookup.AddEmail("existe@a.com", Org, Certa);
        var auth = Criar(new LimitesDeLogin { FalhasParaBloquearEmail = 3, FalhasParaCaptchaConta = 100 });
        var known = new List<Type>();
        var unknown = new List<Type>();

        for (var i = 0; i < 5; i++)
        {
            known.Add((await auth.AutenticarPorEmailAsync("existe@a.com", Errada, De("203.0.113.1"))).GetType());
            unknown.Add((await auth.AutenticarPorEmailAsync("nao-existe@a.com", Errada, De("203.0.113.2"))).GetType());
        }

        Assert.Equal(known, unknown);
        Assert.Equal([typeof(FalhaDeAutenticacao), typeof(FalhaDeAutenticacao), typeof(FalhaDeAutenticacao), typeof(LoginBloqueado), typeof(LoginBloqueado)], known);
    }

    [Fact]
    public async Task A_lock_of_a_known_account_is_audited_and_raises_one_security_event_but_an_unknown_one_leaves_nothing()
    {
        var conta = _lookup.AddEmail("dono@a.com", Org, Certa);
        var auth = Criar(new LimitesDeLogin { FalhasParaBloquearEmail = 2, FalhasParaCaptchaConta = 100 });

        for (var i = 0; i < 4; i++)
        {
            await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1"));
            await auth.AutenticarPorEmailAsync("fantasma@a.com", Errada, De("203.0.113.2"));
        }

        var evento = Assert.IsType<ContaBloqueada>(Assert.Single(_events.Published));
        Assert.Equal(conta.ContaId, evento.ContaId);
        Assert.Equal(Org, evento.OrganizacaoId);
        Assert.Equal("seguranca.conta-bloqueada", evento.EventType);
        Assert.Equal(MotivoDeBloqueio.TentativasDaConta.ToString(), evento.Motivo);
        Assert.Single(_audit.Entries, e => e.Action == IdentidadeAuditActions.AutenticacaoBloquear);
        Assert.Equal(2, _audit.Entries.Count(e => e.Action == IdentidadeAuditActions.AutenticacaoFalhar)); // only the two failures that were really judged
    }

    [Fact]
    public async Task Operational_and_email_accounts_have_their_own_limits()
    {
        _lookup.AddOperacional(Org, "maria", Certa);
        _lookup.AddEmail("dono@a.com", Org, Certa);
        var auth = Criar(new LimitesDeLogin { FalhasParaBloquearEmail = 2, FalhasParaBloquearOperacional = 4, FalhasParaCaptchaConta = 100 });

        for (var i = 0; i < 2; i++)
        {
            await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1"));
            await auth.AutenticarOperacionalAsync(Org, "maria", Errada, De("203.0.113.1"));
        }

        Assert.IsType<LoginBloqueado>(await auth.AutenticarPorEmailAsync("dono@a.com", Certa, De("203.0.113.1")));
        Assert.IsType<Autenticado>(await auth.AutenticarOperacionalAsync(Org, "maria", Certa, De("203.0.113.1")));
    }

    [Fact]
    public async Task Many_accounts_from_one_address_lock_the_address_for_everyone_and_leave_a_technical_log_but_no_outbox_event()
    {
        for (var i = 0; i < 6; i++)
        {
            _lookup.AddEmail($"u{i}@a.com", Org, Certa);
        }

        _lookup.AddEmail("inocente@a.com", Org, Certa);
        var auth = Criar(new LimitesDeLogin { ContasDistintasPorIp = 5, FalhasParaCaptchaConta = 100, FalhasParaCaptchaIp = 100 });

        for (var i = 0; i < 5; i++)
        {
            await auth.AutenticarPorEmailAsync($"u{i}@a.com", Errada, De("198.51.100.7"));
        }

        Assert.IsType<LoginBloqueado>(await auth.AutenticarPorEmailAsync("inocente@a.com", Certa, De("198.51.100.7")));
        Assert.IsType<Autenticado>(await auth.AutenticarPorEmailAsync("inocente@a.com", Certa, De("198.51.100.8"))); // another address works
        Assert.Empty(_events.Published); // an address has no organization: no outbox event, no audit chain
        var linha = Assert.Single(_log.Lines, l => l.Name == "login.ip-bloqueado");
        Assert.Equal(LogSeverity.Warning, linha.Severity);
        Assert.DoesNotContain("198.51.100.7", string.Join(' ', linha.Fields!.Values.Select(v => v?.ToString())), StringComparison.Ordinal); // not even the address is logged as is
    }

    [Fact]
    public async Task One_account_hit_from_many_addresses_is_locked_and_raises_the_event_with_that_reason()
    {
        var conta = _lookup.AddEmail("dono@a.com", Org, Certa);
        var auth = Criar(new LimitesDeLogin { IpsDistintosPorConta = 3, FalhasParaBloquearEmail = 100, FalhasParaCaptchaConta = 100 });

        for (var i = 0; i < 3; i++)
        {
            await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De($"198.51.100.{i}"));
        }

        Assert.IsType<LoginBloqueado>(await auth.AutenticarPorEmailAsync("dono@a.com", Certa, De("203.0.113.50")));
        var evento = Assert.IsType<ContaBloqueada>(Assert.Single(_events.Published));
        Assert.Equal(conta.ContaId, evento.ContaId);
        Assert.Equal(MotivoDeBloqueio.IpsDemaisPorConta.ToString(), evento.Motivo);
    }

    [Fact]
    public async Task A_success_clears_the_failures_of_the_account()
    {
        _lookup.AddEmail("dono@a.com", Org, Certa);
        var auth = Criar(new LimitesDeLogin { FalhasParaBloquearEmail = 3, FalhasParaCaptchaConta = 100 });
        await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1"));
        await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1"));
        Assert.IsType<Autenticado>(await auth.AutenticarPorEmailAsync("dono@a.com", Certa, De("203.0.113.1")));

        await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1"));
        await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1"));

        Assert.IsType<Autenticado>(await auth.AutenticarPorEmailAsync("dono@a.com", Certa, De("203.0.113.1")));
    }

    [Fact]
    public async Task With_a_captcha_provider_the_login_asks_for_it_after_a_few_failures_and_a_wrong_answer_does_not_count_as_a_failure()
    {
        _lookup.AddEmail("dono@a.com", Org, Certa);
        var captcha = new FakeCaptcha(configurado: true, aceita: true);
        var auth = Criar(new LimitesDeLogin { FalhasParaCaptchaConta = 2, FalhasParaBloquearEmail = 3 }, captcha);
        await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1"));
        await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1"));

        Assert.IsType<CaptchaExigido>(await auth.AutenticarPorEmailAsync("dono@a.com", Certa, De("203.0.113.1")));
        Assert.IsType<CaptchaExigido>(await auth.AutenticarPorEmailAsync("dono@a.com", Certa, De("203.0.113.1", "errado")));
        Assert.IsType<CaptchaExigido>(await auth.AutenticarPorEmailAsync("dono@a.com", Certa, De("203.0.113.1", "errado")));
        var passed = await auth.AutenticarPorEmailAsync("dono@a.com", Certa, De("203.0.113.1", "resolvido"));

        Assert.IsType<Autenticado>(passed); // the three CAPTCHA refusals did not lock the account (limit 3 failures, 2 counted)
        Assert.Equal(3, _hasher.Work); // 2 failures + 1 success hashed; the refused ones did no hashing
    }

    [Fact]
    public async Task Without_a_captcha_provider_the_login_never_asks_for_one()
    {
        _lookup.AddEmail("dono@a.com", Org, Certa);
        var auth = Criar(new LimitesDeLogin { FalhasParaCaptchaConta = 1, FalhasParaBloquearEmail = 10 });
        await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1"));
        await auth.AutenticarPorEmailAsync("dono@a.com", Errada, De("203.0.113.1"));

        Assert.IsType<Autenticado>(await auth.AutenticarPorEmailAsync("dono@a.com", Certa, De("203.0.113.1")));
    }

    [Fact]
    public async Task The_calls_without_a_context_still_work_and_still_count_by_account()
    {
        _lookup.AddEmail("dono@a.com", Org, Certa);
        var auth = Criar(new LimitesDeLogin { FalhasParaBloquearEmail = 2, FalhasParaCaptchaConta = 100 });
        await auth.AutenticarPorEmailAsync("dono@a.com", Errada);
        await auth.AutenticarPorEmailAsync("dono@a.com", Errada);

        Assert.IsType<LoginBloqueado>(await auth.AutenticarPorEmailAsync("dono@a.com", Certa));
    }

    [Fact]
    public async Task The_identifier_is_normalized_before_counting_so_spelling_does_not_dodge_the_lock()
    {
        _lookup.AddEmail("dono@a.com", Org, Certa);
        var auth = Criar(new LimitesDeLogin { FalhasParaBloquearEmail = 3, FalhasParaCaptchaConta = 100 });
        await auth.AutenticarPorEmailAsync("dono@a.com", Errada);
        await auth.AutenticarPorEmailAsync(" DONO@a.com ", Errada);
        await auth.AutenticarPorEmailAsync("Dono@A.com", Errada);

        Assert.IsType<LoginBloqueado>(await auth.AutenticarPorEmailAsync("dono@a.com", Certa));
    }
}
