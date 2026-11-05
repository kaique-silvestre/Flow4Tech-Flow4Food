using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Tests;

internal sealed class FakeProvedorOidc : IProvedorOidc
{
    public bool Configurado { get; set; } = true;

    public IdentidadeOidc? Identidade { get; set; }

    public List<(string Code, string Verifier, string Nonce)> Trocas { get; } = [];

    public Uri UrlDeAutorizacao(string state, string nonce, string codeChallenge) =>
        new($"https://provedor.invalido/auth?state={Uri.EscapeDataString(state)}&nonce={Uri.EscapeDataString(nonce)}&code_challenge={Uri.EscapeDataString(codeChallenge)}");

    public Task<IdentidadeOidc?> TrocarCodigoAsync(string code, string codeVerifier, string nonceEsperado, CancellationToken cancellationToken)
    {
        Trocas.Add((code, codeVerifier, nonceEsperado));
        return Task.FromResult(code == "bom" ? Identidade : null);
    }
}

/// <summary>
/// Spec 5.2.3 (ticket 09): Google only with a verified e-mail, never creates an account, linking needs the signed-in person's confirmation, an operational account never uses it.
/// The provider is a fake (no network); the real provider is covered by <c>ProvedorOidcGoogleTests</c> against a fake HTTP server.
/// </summary>
public sealed class ContasGoogleTests
{
    private static readonly Guid Org = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private const string Senha = "senha-atual-da-maria-2026";

    private readonly FixedClock _clock = new();
    private readonly FakeContas _contas = new();
    private readonly FakeLookup _lookup = new();
    private readonly FakeVinculos _vinculos = new();
    private readonly FakeProvedorOidc _provedor = new();
    private readonly RecordingAudit _audit = new();
    private readonly CountingFloor _floor = new();
    private readonly LimitadorDeLoginEmMemoria _limitador;
    private readonly SeloDeTransacaoHmac _selo;
    private readonly Conta _maria;

    public ContasGoogleTests()
    {
        _limitador = new LimitadorDeLoginEmMemoria(_clock, new LimitesDeLogin());
        _selo = new SeloDeTransacaoHmac("uma-chave-de-teste-com-mais-de-32-bytes!!", _clock);
        _maria = _contas.Adicionar(Org, TipoConta.Email, Senha, "maria@a.com");
        _lookup.AddEmailExistente("maria@a.com", new ContaParaLogin(_maria.Id, Org, TipoConta.Email, "hash:" + Senha, false, true, 1, null));
        _provedor.Identidade = new IdentidadeOidc("sub-maria", "maria@a.com", true);
    }

    private ContasGoogle Criar() => new(_provedor, _selo, _lookup, _contas, _vinculos, new FakeHasher(), new FakeUnitOfWork(), _audit, _limitador, _floor, _clock, new RecordingLog());

    private static FakeRequest Como(Conta c) => new(c.Id, new OrganizationContext(Org, Guid.NewGuid()));

    private static (string State, string Nonce) Parametros(InicioGoogle inicio)
    {
        var query = System.Web.HttpUtility.ParseQueryString(inicio.Url.Query);
        return (query["state"]!, query["nonce"]!);
    }

    private static async Task<(string Selo, string State)> VinculoIniciadoAsync(ContasGoogle servico, Conta conta)
    {
        var inicio = await servico.IniciarVinculoAsync(Como(conta), Senha);
        Assert.Equal(StatusInicioVinculo.Iniciado, inicio.Status);
        return (inicio.Inicio!.Selo, Parametros(inicio.Inicio).State);
    }

    // ---- sign in -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Starting_sign_in_builds_the_authorization_request_with_state_nonce_and_pkce()
    {
        var inicio = Criar().IniciarEntrada()!;

        var (state, nonce) = Parametros(inicio);
        Assert.False(string.IsNullOrEmpty(state));
        Assert.False(string.IsNullOrEmpty(nonce));
        Assert.NotEqual(state, nonce);
        var transacao = _selo.Abrir(inicio.Selo)!;
        Assert.Equal("entrar", transacao.Proposito);
        Assert.Equal(state, transacao.State);
        Assert.Equal(nonce, transacao.Nonce);
        Assert.Contains("code_challenge=", inicio.Url.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(transacao.Verifier, inicio.Url.ToString(), StringComparison.Ordinal); // the verifier never travels to the provider's URL
    }

    [Fact]
    public void Without_a_configured_provider_nothing_starts()
    {
        _provedor.Configurado = false;

        Assert.Null(Criar().IniciarEntrada());
    }

    [Fact]
    public async Task A_linked_account_signs_in_and_the_provider_got_the_code_verifier_and_nonce()
    {
        _vinculos.Linhas[(_maria.Id, "google")] = "sub-maria";
        var servico = Criar();
        var inicio = servico.IniciarEntrada()!;
        var (state, nonce) = Parametros(inicio);

        var result = await servico.ConcluirAsync(inicio.Selo, state, "bom", ContextoDeLogin.Nenhum);

        var entrou = Assert.IsType<GoogleEntrou>(result);
        Assert.Equal(_maria.Id, entrou.Conta.ContaId);
        Assert.Equal(Org, entrou.Conta.OrganizacaoId);
        var troca = Assert.Single(_provedor.Trocas);
        Assert.Equal(nonce, troca.Nonce);
        Assert.Equal(_selo.Abrir(inicio.Selo)!.Verifier, troca.Verifier);
        Assert.Equal(1, _floor.Calls);
    }

    [Fact]
    public async Task An_unlinked_unknown_or_unverified_google_account_gets_the_same_neutral_answer_and_no_account_is_created()
    {
        var antes = _contas.Linhas.Count;
        var servico = Criar();

        // 1. a Google account whose verified e-mail belongs to an account, but never linked
        var semVinculo = await Entrar(servico, new IdentidadeOidc("sub-maria", "maria@a.com", true));
        // 2. a Google account nobody here knows
        var desconhecida = await Entrar(servico, new IdentidadeOidc("sub-x", "estranho@a.com", true));
        // 3. e-mail not verified by Google (even if it matches a linked account)
        _vinculos.Linhas[(_maria.Id, "google")] = "sub-maria";
        var naoVerificado = await Entrar(servico, new IdentidadeOidc("sub-maria", "maria@a.com", false));
        // 4. no e-mail at all
        var semEmail = await Entrar(servico, new IdentidadeOidc("sub-maria", null, true));

        Assert.All(new[] { semVinculo, desconhecida, naoVerificado, semEmail }, r => Assert.IsType<GoogleNeutro>(r));
        Assert.Equal(antes, _contas.Linhas.Count); // NEVER creates an account
        Assert.Single(_vinculos.Linhas);           // nor a link
    }

    private async Task<ResultadoGoogle> Entrar(ContasGoogle servico, IdentidadeOidc identidade)
    {
        _provedor.Identidade = identidade;
        var inicio = servico.IniciarEntrada()!;
        return await servico.ConcluirAsync(inicio.Selo, Parametros(inicio).State, "bom", ContextoDeLogin.Nenhum);
    }

    [Fact]
    public async Task A_different_google_identity_with_the_same_e_mail_does_not_get_in()
    {
        _vinculos.Linhas[(_maria.Id, "google")] = "sub-da-maria";

        var result = await Entrar(Criar(), new IdentidadeOidc("sub-de-outra-pessoa", "maria@a.com", true));

        Assert.IsType<GoogleNeutro>(result);
        Assert.Contains(_audit.Entries, e => e.Action == IdentidadeAuditActions.AutenticacaoFalhar && e.Result == AuditResult.Negado && e.Detached);
    }

    [Fact]
    public async Task Inactive_operational_and_must_change_password_accounts_never_sign_in_with_google()
    {
        var inativa = _contas.Adicionar(Org, TipoConta.Email, "x", "inativa@a.com", ativa: false);
        _lookup.AddEmailExistente("inativa@a.com", new ContaParaLogin(inativa.Id, Org, TipoConta.Email, "h", false, false, 1, null));
        var operacional = _contas.Adicionar(Org, TipoConta.Operacional, "x", usuario: "joao");
        _lookup.AddEmailExistente("op@a.com", new ContaParaLogin(operacional.Id, Org, TipoConta.Operacional, "h", false, true, 1, null));
        var temporaria = _contas.Adicionar(Org, TipoConta.Email, "x", "temp@a.com", deveTrocar: true);
        _lookup.AddEmailExistente("temp@a.com", new ContaParaLogin(temporaria.Id, Org, TipoConta.Email, "h", true, true, 1, null));
        foreach (var conta in new[] { inativa, operacional, temporaria })
        {
            _vinculos.Linhas[(conta.Id, "google")] = "sub-" + conta.Id;
        }

        var servico = Criar();
        Assert.IsType<GoogleNeutro>(await Entrar(servico, new IdentidadeOidc("sub-" + inativa.Id, "inativa@a.com", true)));
        Assert.IsType<GoogleNeutro>(await Entrar(servico, new IdentidadeOidc("sub-" + operacional.Id, "op@a.com", true)));
        Assert.IsType<GoogleNeutro>(await Entrar(servico, new IdentidadeOidc("sub-" + temporaria.Id, "temp@a.com", true)));
    }

    [Fact]
    public async Task A_tampered_expired_missing_or_mismatched_transaction_stops_before_the_provider_is_called()
    {
        _vinculos.Linhas[(_maria.Id, "google")] = "sub-maria";
        var servico = Criar();
        var inicio = servico.IniciarEntrada()!;
        var (state, _) = Parametros(inicio);

        var adulterado = inicio.Selo[..^3] + (inicio.Selo.EndsWith("AAA", StringComparison.Ordinal) ? "BBB" : "AAA");
        Assert.IsType<GoogleNeutro>(await servico.ConcluirAsync(adulterado, state, "bom", ContextoDeLogin.Nenhum));
        Assert.IsType<GoogleNeutro>(await servico.ConcluirAsync(inicio.Selo, "outro-state", "bom", ContextoDeLogin.Nenhum));
        Assert.IsType<GoogleNeutro>(await servico.ConcluirAsync(null, state, "bom", ContextoDeLogin.Nenhum));
        Assert.IsType<GoogleNeutro>(await servico.ConcluirAsync(inicio.Selo, null, "bom", ContextoDeLogin.Nenhum));
        Assert.IsType<GoogleNeutro>(await servico.ConcluirAsync(inicio.Selo, state, null, ContextoDeLogin.Nenhum));
        _clock.UtcNow += TimeSpan.FromMinutes(11);
        Assert.IsType<GoogleNeutro>(await servico.ConcluirAsync(inicio.Selo, state, "bom", ContextoDeLogin.Nenhum));

        Assert.Empty(_provedor.Trocas);
    }

    [Fact]
    public async Task A_code_the_provider_refuses_is_neutral()
    {
        _vinculos.Linhas[(_maria.Id, "google")] = "sub-maria";
        var servico = Criar();
        var inicio = servico.IniciarEntrada()!;

        Assert.IsType<GoogleNeutro>(await servico.ConcluirAsync(inicio.Selo, Parametros(inicio).State, "ruim", ContextoDeLogin.Nenhum));
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_for_google_too_with_the_login_limiter()
    {
        _vinculos.Linhas[(_maria.Id, "google")] = "sub-da-maria";
        var servico = Criar();
        for (var i = 0; i < 5; i++)
        {
            await Entrar(servico, new IdentidadeOidc("sub-invasor", "maria@a.com", true));
        }

        _vinculos.Linhas[(_maria.Id, "google")] = "sub-maria";
        var result = await Entrar(servico, new IdentidadeOidc("sub-maria", "maria@a.com", true));

        Assert.IsType<GoogleBloqueado>(result); // same key as the password login: a stranger cannot try Google identities forever
    }

    // ---- linking ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Linking_needs_the_current_password_and_an_account_with_e_mail()
    {
        var servico = Criar();
        var operacional = _contas.Adicionar(Org, TipoConta.Operacional, "x", usuario: "joao");

        Assert.Equal(StatusInicioVinculo.SenhaInvalida, (await servico.IniciarVinculoAsync(Como(_maria), "senha-errada-123")).Status);
        Assert.Equal(StatusInicioVinculo.ContaNaoElegivel, (await servico.IniciarVinculoAsync(Como(operacional), "x")).Status);
        Assert.Equal(StatusInicioVinculo.Iniciado, (await servico.IniciarVinculoAsync(Como(_maria), Senha)).Status);
        _provedor.Configurado = false;
        Assert.Equal(StatusInicioVinculo.NaoConfigurado, (await Criar().IniciarVinculoAsync(Como(_maria), Senha)).Status);
    }

    [Fact]
    public async Task Guessing_the_password_through_the_link_confirmation_is_throttled()
    {
        var servico = Criar();
        for (var i = 0; i < 5; i++)
        {
            await servico.IniciarVinculoAsync(Como(_maria), "senha-errada-123");
        }

        Assert.Equal(StatusInicioVinculo.Bloqueado, (await servico.IniciarVinculoAsync(Como(_maria), Senha)).Status);
    }

    [Fact]
    public async Task Confirmed_linking_stores_the_google_identity_audits_it_and_never_starts_a_session()
    {
        var servico = Criar();
        var (selo, state) = await VinculoIniciadoAsync(servico, _maria);

        var result = await servico.ConcluirAsync(selo, state, "bom", ContextoDeLogin.Nenhum);

        Assert.IsType<GoogleVinculado>(result);
        Assert.Equal("sub-maria", _vinculos.Linhas[(_maria.Id, "google")]);
        Assert.Contains(_audit.Entries, e => e.Action == IdentidadeAuditActions.ContaVincularGoogle && e.Result == AuditResult.Sucesso);
        // and afterwards the sign-in works
        Assert.IsType<GoogleEntrou>(await Entrar(servico, new IdentidadeOidc("sub-maria", "maria@a.com", true)));
    }

    [Fact]
    public async Task Linking_requires_the_google_e_mail_to_be_verified_and_to_be_the_account_e_mail()
    {
        var servico = Criar();
        foreach (var identidade in new[] { new IdentidadeOidc("sub-x", "outro@gmail.com", true), new IdentidadeOidc("sub-x", "maria@a.com", false), new IdentidadeOidc("sub-x", null, true) })
        {
            _provedor.Identidade = identidade;
            var (selo, state) = await VinculoIniciadoAsync(servico, _maria);
            Assert.IsType<GoogleNeutro>(await servico.ConcluirAsync(selo, state, "bom", ContextoDeLogin.Nenhum));
        }

        Assert.Empty(_vinculos.Linhas);
    }

    [Fact]
    public async Task A_google_identity_already_linked_elsewhere_or_a_second_identity_for_the_account_is_neutral()
    {
        var servico = Criar();
        var outra = _contas.Adicionar(Org, TipoConta.Email, "x", "outra@a.com");
        _vinculos.Linhas[(outra.Id, "google")] = "sub-maria";
        var (selo, state) = await VinculoIniciadoAsync(servico, _maria);
        Assert.IsType<GoogleNeutro>(await servico.ConcluirAsync(selo, state, "bom", ContextoDeLogin.Nenhum));

        _vinculos.Linhas.Remove((outra.Id, "google"));
        _vinculos.Linhas[(_maria.Id, "google")] = "outro-sub";
        (selo, state) = await VinculoIniciadoAsync(servico, _maria);
        Assert.IsType<GoogleNeutro>(await servico.ConcluirAsync(selo, state, "bom", ContextoDeLogin.Nenhum));
    }

    [Fact]
    public async Task A_password_change_between_the_confirmation_and_the_callback_cancels_the_linking()
    {
        var servico = Criar();
        var (selo, state) = await VinculoIniciadoAsync(servico, _maria);
        await _contas.TrocarSenhaAsync(Org, _maria.Id, "hash:nova", default); // bumps the credential version

        Assert.IsType<GoogleNeutro>(await servico.ConcluirAsync(selo, state, "bom", ContextoDeLogin.Nenhum));
        Assert.Empty(_vinculos.Linhas);
    }

    [Fact]
    public async Task Unlinking_removes_the_identity_and_is_audited()
    {
        _vinculos.Linhas[(_maria.Id, "google")] = "sub-maria";
        var servico = Criar();

        Assert.True(await servico.DesvincularAsync(Como(_maria)));
        Assert.False(await servico.DesvincularAsync(Como(_maria)));
        Assert.Contains(_audit.Entries, e => e.Action == IdentidadeAuditActions.ContaDesvincularGoogle);
        Assert.IsType<GoogleNeutro>(await Entrar(servico, new IdentidadeOidc("sub-maria", "maria@a.com", true)));
    }

    [Fact]
    public void The_sealed_transaction_is_signed_expires_and_never_shows_the_code_verifier_unsigned()
    {
        var transacao = new TransacaoOidc("entrar", "s", "n", "v", _clock.UtcNow + TimeSpan.FromMinutes(10), null, null, null);
        var selo = _selo.Selar(transacao);

        Assert.Equal(transacao, _selo.Abrir(selo));
        Assert.Null(_selo.Abrir(selo + "x"));
        Assert.Null(new SeloDeTransacaoHmac("outra-chave-de-teste-com-mais-de-32-bytes!!", _clock).Abrir(selo));
        Assert.Null(_selo.Abrir("lixo.lixo"));
        Assert.Null(_selo.Abrir(new string('a', 5000)));
        _clock.UtcNow += TimeSpan.FromMinutes(11);
        Assert.Null(_selo.Abrir(selo));
    }
}
