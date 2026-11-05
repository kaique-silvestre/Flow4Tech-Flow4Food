using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Tests;

/// <summary>
/// Spec 5.2.5 and 5.2.8 (ticket 09): recovery by e-mail without enumeration (identical answer and work whether the account exists or not, throttled by the login limiter), single-use
/// short-lived token stored as a hash, every session ended, the person told. No database.
/// </summary>
public sealed class RecuperacaoDeAcessoTests
{
    private static readonly Guid Org = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private const string NovaSenha = "outra-senha-bem-longa-2026";

    private readonly FixedClock _clock = new();
    private readonly FakeContas _contas = new();
    private readonly FakeLookup _lookup = new();
    private readonly FakeRecuperacoes _recuperacoes = new();
    private readonly FakeRevogacao _revogacao = new();
    private readonly RecordingAudit _audit = new();
    private readonly RecordingEvents _events = new();
    private readonly FakeEmail _email = new();
    private readonly RecordingLog _log = new();
    private readonly CountingFloor _floor = new();
    private readonly LimitadorDeLoginEmMemoria _limitador;
    private bool _vazada;

    public RecuperacaoDeAcessoTests() => _limitador = new LimitadorDeLoginEmMemoria(_clock, new LimitesDeLogin());

    private RecuperacaoDeAcesso Criar() =>
        new(new FakeUnitOfWork(), _lookup, _contas, _recuperacoes, _revogacao, new FakeHasher(), new FakeCompromised(_vazada), _audit, _events, _email, _limitador, _floor, _log, _clock, new OpcoesDeAcesso(TimeSpan.FromDays(7), TimeSpan.FromHours(1), new Uri("https://app.exemplo.com")));

    private Conta ContaComEmail(string email = "maria@a.com", bool ativa = true)
    {
        var conta = _contas.Adicionar(Org, TipoConta.Email, "senha-antiga-123", email, ativa: ativa);
        _lookup.AddEmailExistente(email, new ContaParaLogin(conta.Id, Org, TipoConta.Email, "hash:senha-antiga-123", false, ativa, 1, null));
        return conta;
    }

    private string TokenEnviado() => _email.Enviadas.Single(m => m.Assunto.Contains("Recupera", StringComparison.Ordinal)).Texto.Split("#token=")[1].Split(['\n', ' '])[0];

    [Fact]
    public async Task A_known_account_gets_a_mail_with_a_link_and_only_the_hash_is_stored()
    {
        var conta = ContaComEmail();

        await Criar().SolicitarAsync("Maria@A.com", ContextoDeLogin.Nenhum);

        var token = TokenEnviado();
        Assert.StartsWith("a1." + Org.ToString("N"), token, StringComparison.Ordinal);
        var linha = Assert.Single(_recuperacoes.Linhas);
        Assert.Equal(conta.Id, linha.ContaId);
        Assert.Equal(32, linha.Hash.Length);
        Assert.Equal(_clock.UtcNow + TimeSpan.FromHours(1), linha.ExpiraEm);
        Assert.Equal("maria@a.com", _email.Enviadas.Single().Para);
        Assert.Contains(_audit.Entries, e => e.Action == IdentidadeAuditActions.ContaSolicitarRecuperacao);
    }

    [Theory]
    [InlineData("desconhecida@a.com")]
    [InlineData("nao-e-email")]
    [InlineData("")]
    public async Task An_unknown_address_does_the_same_visible_work_and_sends_nothing(string email)
    {
        await Criar().SolicitarAsync(email, ContextoDeLogin.Nenhum);

        Assert.Empty(_email.Enviadas);
        Assert.Empty(_recuperacoes.Linhas);
        Assert.Equal(1, _floor.Calls); // the response-time floor is applied to this path exactly as to the known one
    }

    [Fact]
    public async Task Known_unknown_and_inactive_accounts_are_indistinguishable_from_outside()
    {
        ContaComEmail();
        ContaComEmail("inativa@a.com", ativa: false);
        var operacional = _contas.Adicionar(Org, TipoConta.Operacional, "x", usuario: "joao");
        _lookup.AddEmailExistente("op@a.com", new ContaParaLogin(operacional.Id, Org, TipoConta.Operacional, "hash:x", false, true, 1, null));

        foreach (var email in new[] { "maria@a.com", "inativa@a.com", "op@a.com", "ninguem@a.com" })
        {
            await Criar().SolicitarAsync(email, ContextoDeLogin.Nenhum); // returns nothing: there is no output to tell them apart
        }

        Assert.Equal(4, _floor.Calls);
        Assert.Equal("maria@a.com", Assert.Single(_email.Enviadas).Para); // only the active account with e-mail
    }

    [Fact]
    public async Task A_new_request_supersedes_the_previous_token()
    {
        ContaComEmail();
        var servico = Criar();
        await servico.SolicitarAsync("maria@a.com", ContextoDeLogin.Nenhum);
        var velho = TokenEnviado();
        _email.Enviadas.Clear();
        await servico.SolicitarAsync("maria@a.com", ContextoDeLogin.Nenhum);

        Assert.Equal(StatusRecuperacao.TokenInvalido, (await servico.RedefinirAsync(velho, NovaSenha)).Status);
        Assert.Equal(StatusRecuperacao.Redefinida, (await servico.RedefinirAsync(TokenEnviado(), NovaSenha)).Status);
    }

    [Fact]
    public async Task Hammering_the_endpoint_is_throttled_silently_with_the_login_limiter()
    {
        ContaComEmail();
        var servico = Criar();

        for (var i = 0; i < 12; i++)
        {
            await servico.SolicitarAsync("maria@a.com", new ContextoDeLogin("203.0.113.9", null));
        }

        Assert.True(_email.Enviadas.Count is >= 1 and < 12); // the mailbox of the victim is not flooded
        Assert.Equal(12, _floor.Calls);                     // and every call still waits the same floor
    }

    [Fact]
    public async Task Throttling_by_address_also_covers_unknown_addresses()
    {
        var servico = Criar();

        for (var i = 0; i < 40; i++)
        {
            await servico.SolicitarAsync($"x{i}@a.com", new ContextoDeLogin("203.0.113.9", null));
        }

        Assert.IsType<Bloqueada>(_limitador.Avaliar(new ChaveDeLogin(TipoConta.Email, "qualquer"), "203.0.113.9"));
        Assert.Empty(_email.Enviadas);
    }

    [Fact]
    public async Task Redeeming_sets_the_new_password_ends_every_session_notifies_and_unlocks()
    {
        var conta = ContaComEmail();
        var servico = Criar();
        await servico.SolicitarAsync("maria@a.com", ContextoDeLogin.Nenhum);
        var chaveDeLogin = new ChaveDeLogin(TipoConta.Email, "maria@a.com");
        for (var i = 0; i < 6; i++)
        {
            _limitador.RegistrarFalha(chaveDeLogin, null);
        }

        Assert.IsType<Bloqueada>(_limitador.Avaliar(chaveDeLogin, null));

        var result = await servico.RedefinirAsync(TokenEnviado(), NovaSenha);

        Assert.Equal(StatusRecuperacao.Redefinida, result.Status);
        Assert.Equal("hash:" + NovaSenha, _contas.Linhas[conta.Id].Hash);
        Assert.Equal(2, _contas.Linhas[conta.Id].Conta.VersaoCredencial); // bump = every session of the old password dies on its next request
        Assert.Contains(_revogacao.Revogadas, r => r.Conta == conta.Id && r.Org == Org);
        Assert.Contains(_audit.Entries, e => e.Action == IdentidadeAuditActions.ContaRecuperarAcesso && e.Result == AuditResult.Sucesso);
        var evento = Assert.IsType<AcessoRecuperado>(Assert.Single(_events.Published));
        Assert.Equal(conta.Id, evento.ContaId);
        Assert.IsType<Livre>(_limitador.Avaliar(chaveDeLogin, null)); // the way out of the lock a stranger caused (ticket 08)
    }

    [Fact]
    public async Task The_token_works_once()
    {
        ContaComEmail();
        var servico = Criar();
        await servico.SolicitarAsync("maria@a.com", ContextoDeLogin.Nenhum);
        var token = TokenEnviado();

        Assert.Equal(StatusRecuperacao.Redefinida, (await servico.RedefinirAsync(token, NovaSenha)).Status);
        Assert.Equal(StatusRecuperacao.TokenInvalido, (await servico.RedefinirAsync(token, "mais-uma-senha-longa-2027")).Status);
    }

    [Fact]
    public async Task An_expired_unknown_or_malformed_token_gets_the_same_answer()
    {
        ContaComEmail();
        var servico = Criar();
        await servico.SolicitarAsync("maria@a.com", ContextoDeLogin.Nenhum);
        var token = TokenEnviado();
        _clock.UtcNow += TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1);

        var respostas = new[]
        {
            await servico.RedefinirAsync(token, NovaSenha),
            await servico.RedefinirAsync("a1." + Org.ToString("N") + "." + new string('B', 43), NovaSenha),
            await servico.RedefinirAsync("lixo", NovaSenha),
            await servico.RedefinirAsync("c1." + Org.ToString("N") + "." + new string('B', 43), NovaSenha),
        };

        Assert.All(respostas, r => Assert.Equal(StatusRecuperacao.TokenInvalido, r.Status));
        Assert.Empty(_contas.SenhasTrocadas);
    }

    [Fact]
    public async Task A_token_of_an_account_deactivated_meanwhile_is_dead()
    {
        var conta = ContaComEmail();
        var servico = Criar();
        await servico.SolicitarAsync("maria@a.com", ContextoDeLogin.Nenhum);
        var linha = _contas.Linhas[conta.Id];
        _contas.Linhas[conta.Id] = (linha.Conta with { Ativa = false }, linha.Hash, linha.DeveTrocar, linha.Expira);

        Assert.Equal(StatusRecuperacao.TokenInvalido, (await servico.RedefinirAsync(TokenEnviado(), NovaSenha)).Status);
        Assert.Empty(_contas.SenhasTrocadas);
    }

    [Fact]
    public async Task A_weak_leaked_or_oversized_password_does_not_burn_the_token()
    {
        ContaComEmail();
        var servico = Criar();
        await servico.SolicitarAsync("maria@a.com", ContextoDeLogin.Nenhum);
        var token = TokenEnviado();

        var curta = await servico.RedefinirAsync(token, "curta");
        var enorme = await servico.RedefinirAsync(token, new string('x', PoliticaDeSenha.LimiteDeEntrada + 1));
        _vazada = true;
        var vazada = await Criar().RedefinirAsync(token, NovaSenha);
        _vazada = false;

        Assert.Equal(StatusRecuperacao.SenhaInvalida, curta.Status);
        Assert.Equal(StatusRecuperacao.SenhaInvalida, enorme.Status);
        Assert.Equal(StatusRecuperacao.SenhaVazada, vazada.Status);
        Assert.Equal(StatusRecuperacao.Redefinida, (await servico.RedefinirAsync(token, NovaSenha)).Status);
    }

    [Fact]
    public async Task The_password_may_not_be_the_e_mail_of_the_account()
    {
        ContaComEmail("maria.silva@a.com");
        var servico = Criar();
        await servico.SolicitarAsync("maria.silva@a.com", ContextoDeLogin.Nenhum);

        var result = await servico.RedefinirAsync(TokenEnviado(), "maria.silva@a.com");

        Assert.Equal(StatusRecuperacao.SenhaInvalida, result.Status);
        Assert.Contains(ProblemaDeSenha.IgualAoIdentificador, result.Problemas);
    }

    [Fact]
    public async Task A_mail_failure_does_not_change_the_visible_outcome()
    {
        ContaComEmail();
        _email.Falha = true;

        await Criar().SolicitarAsync("maria@a.com", ContextoDeLogin.Nenhum); // does not throw

        Assert.Contains(_log.Lines, l => l.Name == "recuperacao.email-falhou");
        Assert.Equal(1, _floor.Calls);
    }

    [Fact]
    public async Task Nothing_secret_reaches_the_log()
    {
        ContaComEmail();
        await Criar().SolicitarAsync("maria@a.com", ContextoDeLogin.Nenhum);
        var token = TokenEnviado();
        await Criar().RedefinirAsync(token, NovaSenha);

        var tudo = string.Join('\n', _log.Lines.Select(l => l.Name + string.Join(';', l.Fields?.Values.Select(v => v?.ToString()) ?? [])));
        Assert.DoesNotContain(token, tudo, StringComparison.Ordinal);
        Assert.DoesNotContain("maria@a.com", tudo, StringComparison.Ordinal);
        Assert.DoesNotContain(NovaSenha, tudo, StringComparison.Ordinal);
    }
}
