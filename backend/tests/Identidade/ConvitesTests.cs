using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Spec 5.2.1 and 5.2.6 (ticket 09): single-use invitation with expiry, stored as a hash, accepted with the person's OWN password. No database.</summary>
public sealed class ConvitesTests
{
    private static readonly Guid Org = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private const string Senha = "uma-senha-bem-longa-2026";

    private readonly FixedClock _clock = new();
    private readonly FakeContas _contas = new();
    private readonly FakeConvites _convites = new();
    private readonly RecordingAudit _audit = new();
    private readonly RecordingEvents _events = new();
    private readonly FakeEmail _email = new();
    private readonly RecordingLog _log = new();
    private readonly Conta _dono;
    private readonly FakeRequest _request;
    private bool _vazada;

    public ConvitesTests()
    {
        _dono = _contas.Adicionar(Org, TipoConta.Email, "x", "dono@a.com", dono: true);
        _request = new FakeRequest(_dono.Id, new OrganizationContext(Org, Guid.NewGuid()));
    }

    private Convites Criar() =>
        new(new FakeUnitOfWork(), _convites, _contas, new FakeHasher(), new FakeCompromised(_vazada), _audit, _events, _email, _log, _clock, new OpcoesDeAcesso(TimeSpan.FromDays(7), TimeSpan.FromHours(1), new Uri("https://app.exemplo.com")));

    private static string TokenDoEmail(FakeEmail email) => email.Enviadas.Single().Texto.Split("#token=")[1].Split(['\n', ' '])[0];

    [Fact]
    public async Task Inviting_stores_only_the_hash_and_mails_the_link()
    {
        var result = await Criar().ConvidarAsync(_request, "Nova@Exemplo.com ", "Nova Pessoa", null, null);

        Assert.Equal(StatusConvite.Criado, result.Status);
        Assert.True(result.EmailEnviado);
        var token = TokenDoEmail(_email);
        Assert.StartsWith("c1." + Org.ToString("N"), token, StringComparison.Ordinal);
        var stored = Assert.Single(_convites.Linhas).Convite;
        Assert.Equal("nova@exemplo.com", stored.Email);
        Assert.Equal(32, stored.TokenHash.Length);
        Assert.DoesNotContain(token, stored.ToString(), StringComparison.Ordinal);
        Assert.Equal(_clock.UtcNow + TimeSpan.FromDays(7), stored.ExpiraEm);
        Assert.Equal("nova@exemplo.com", _email.Enviadas.Single().Para);
        Assert.Contains(_audit.Entries, e => e.Action == IdentidadeAuditActions.ConviteCriar && e.Result == AuditResult.Sucesso);
    }

    [Fact]
    public async Task The_audit_and_the_log_never_carry_the_token()
    {
        await Criar().ConvidarAsync(_request, "nova@exemplo.com", "Nova", null, null);

        var token = TokenDoEmail(_email);
        Assert.All(_log.Lines, l => Assert.DoesNotContain(token, string.Join(';', l.Fields?.Values.Select(v => v?.ToString()) ?? []), StringComparison.Ordinal));
        Assert.DoesNotContain("[convite", _email.Enviadas.Single().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_invitation_to_the_same_address_revokes_the_old_one()
    {
        var servico = Criar();
        await servico.ConvidarAsync(_request, "nova@exemplo.com", "Nova", null, null);
        var primeiro = TokenDoEmail(_email);
        _email.Enviadas.Clear();
        await servico.ConvidarAsync(_request, "nova@exemplo.com", "Nova", null, null);

        Assert.Equal(StatusAceite.TokenInvalido, (await servico.AceitarAsync(primeiro, Senha)).Status);
        Assert.Equal(StatusAceite.Aceito, (await servico.AceitarAsync(TokenDoEmail(_email), Senha)).Status);
    }

    [Theory]
    [InlineData("nao-e-email")]
    [InlineData("")]
    public async Task An_invalid_address_is_refused(string email)
    {
        Assert.Equal(StatusConvite.EmailInvalido, (await Criar().ConvidarAsync(_request, email, "Nova", null, null)).Status);
        Assert.Empty(_convites.Linhas);
    }

    [Fact]
    public async Task Someone_already_in_the_organization_is_reported_to_the_inviter()
    {
        _convites.MembrosDaOrganizacao.Add("ja@exemplo.com");

        Assert.Equal(StatusConvite.JaEhMembro, (await Criar().ConvidarAsync(_request, "ja@exemplo.com", "Ja", null, null)).Status);
    }

    [Fact]
    public async Task An_actor_who_must_still_change_the_password_or_is_inactive_cannot_invite()
    {
        var temporario = _contas.Adicionar(Org, TipoConta.Email, "x", "t@a.com", deveTrocar: true);
        var inativo = _contas.Adicionar(Org, TipoConta.Email, "x", "i@a.com", ativa: false);

        foreach (var ator in new[] { temporario, inativo })
        {
            var result = await Criar().ConvidarAsync(new FakeRequest(ator.Id, new OrganizationContext(Org, Guid.NewGuid())), "nova@exemplo.com", "Nova", null, null);
            Assert.Equal(StatusConvite.Negado, result.Status);
        }

        Assert.Empty(_convites.Linhas);
        Assert.Empty(_email.Enviadas);
    }

    [Fact]
    public async Task A_profile_or_unit_that_does_not_exist_is_refused_and_nothing_is_mailed()
    {
        _convites.PerfilInvalido = true;

        var result = await Criar().ConvidarAsync(_request, "nova@exemplo.com", "Nova", Guid.NewGuid(), null);

        Assert.Equal(StatusConvite.PerfilOuUnidadeInvalidos, result.Status);
        Assert.Empty(_email.Enviadas);
    }

    [Fact]
    public async Task A_mail_failure_keeps_the_invitation_and_reports_it_without_leaking_the_address()
    {
        _email.Falha = true;

        var result = await Criar().ConvidarAsync(_request, "nova@exemplo.com", "Nova", null, null);

        Assert.Equal(StatusConvite.Criado, result.Status);
        Assert.False(result.EmailEnviado);
        Assert.Single(_convites.Linhas);
        var line = Assert.Single(_log.Lines, l => l.Name == "convite.email-falhou");
        Assert.DoesNotContain("nova@exemplo.com", string.Join(';', line.Fields?.Values.Select(v => v?.ToString()) ?? []), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepting_creates_the_account_with_the_chosen_password_and_uses_the_token_once()
    {
        var servico = Criar();
        await servico.ConvidarAsync(_request, "nova@exemplo.com", "Nova Pessoa", null, null);
        var token = TokenDoEmail(_email);

        var aceito = await servico.AceitarAsync(token, Senha);

        Assert.Equal(StatusAceite.Aceito, aceito.Status);
        var linha = _contas.Linhas[aceito.ContaId!.Value];
        Assert.Equal("nova@exemplo.com", linha.Conta.Email);
        Assert.Equal(TipoConta.Email, linha.Conta.Tipo);
        Assert.False(linha.Conta.Dono);
        Assert.Equal("hash:" + Senha, linha.Hash); // the person's OWN password, never a generated one
        Assert.False(linha.DeveTrocar);
        Assert.Contains(_audit.Entries, e => e.Action == IdentidadeAuditActions.ConviteAceitar);
        var evento = Assert.IsType<ConviteAceito>(Assert.Single(_events.Published));
        Assert.Equal(aceito.ContaId, evento.ContaId);

        // single use
        Assert.Equal(StatusAceite.TokenInvalido, (await servico.AceitarAsync(token, Senha)).Status);
    }

    [Fact]
    public async Task The_event_carries_the_profile_and_unit_of_the_invitation_and_who_invited()
    {
        var perfil = Guid.NewGuid();
        var unidade = Guid.NewGuid();
        var servico = Criar();
        await servico.ConvidarAsync(_request, "nova@exemplo.com", "Nova", perfil, unidade);

        await servico.AceitarAsync(TokenDoEmail(_email), Senha);

        var evento = Assert.IsType<ConviteAceito>(Assert.Single(_events.Published));
        Assert.Equal(perfil, evento.PerfilId);
        Assert.Equal(unidade, evento.UnidadeId);
        Assert.Equal(_dono.Id, evento.ConvidadoPor);
    }

    [Fact]
    public async Task An_expired_revoked_unknown_or_malformed_token_gets_the_same_answer()
    {
        var servico = Criar();
        await servico.ConvidarAsync(_request, "a@exemplo.com", "A", null, null);
        var expirado = TokenDoEmail(_email);
        _email.Enviadas.Clear();
        await servico.ConvidarAsync(_request, "b@exemplo.com", "B", null, null);
        var revogado = TokenDoEmail(_email);
        Assert.True(await servico.RevogarAsync(_request, _convites.Linhas.Single(l => l.Convite.Email == "b@exemplo.com").Convite.Id));

        _clock.UtcNow += TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1);

        var respostas = new[]
        {
            await servico.AceitarAsync(expirado, Senha),
            await servico.AceitarAsync(revogado, Senha),
            await servico.AceitarAsync("c1." + Org.ToString("N") + "." + new string('A', 43), Senha),
            await servico.AceitarAsync("lixo", Senha),
            await servico.AceitarAsync("", Senha),
        };
        Assert.All(respostas, r => Assert.Equal(StatusAceite.TokenInvalido, r.Status));
        Assert.DoesNotContain(_contas.Linhas.Values, l => l.Conta.Email is "a@exemplo.com" or "b@exemplo.com");
    }

    [Fact]
    public async Task A_refresh_token_or_a_recovery_token_is_not_an_invitation()
    {
        var servico = Criar();

        Assert.Equal(StatusAceite.TokenInvalido, (await servico.AceitarAsync("r1." + Org.ToString("N") + "." + new string('A', 43), Senha)).Status);
        Assert.Equal(StatusAceite.TokenInvalido, (await servico.AceitarAsync("a1." + Org.ToString("N") + "." + new string('A', 43), Senha)).Status);
    }

    [Fact]
    public async Task A_weak_or_leaked_password_leaves_the_token_usable()
    {
        var servico = Criar();
        await servico.ConvidarAsync(_request, "nova@exemplo.com", "Nova", null, null);
        var token = TokenDoEmail(_email);

        var curta = await servico.AceitarAsync(token, "curta");
        _vazada = true;
        var vazada = await Criar().AceitarAsync(token, Senha);
        _vazada = false;

        Assert.Equal(StatusAceite.SenhaInvalida, curta.Status);
        Assert.Contains(ProblemaDeSenha.Curta, curta.Problemas);
        Assert.Equal(StatusAceite.SenhaVazada, vazada.Status);
        Assert.Equal(StatusAceite.Aceito, (await servico.AceitarAsync(token, Senha)).Status);
    }

    [Fact]
    public async Task A_password_equal_to_the_e_mail_is_refused()
    {
        var servico = Criar();
        await servico.ConvidarAsync(_request, "nova.pessoa@exemplo.com", "Nova", null, null);

        var result = await servico.AceitarAsync(TokenDoEmail(_email), "nova.pessoa@exemplo.com");

        Assert.Equal(StatusAceite.SenhaInvalida, result.Status);
        Assert.Contains(ProblemaDeSenha.IgualAoIdentificador, result.Problemas);
    }

    [Fact]
    public async Task An_oversized_password_is_refused_before_any_hashing()
    {
        var servico = Criar();
        await servico.ConvidarAsync(_request, "nova@exemplo.com", "Nova", null, null);

        var result = await servico.AceitarAsync(TokenDoEmail(_email), new string('x', PoliticaDeSenha.LimiteDeEntrada + 1));

        Assert.Equal(StatusAceite.SenhaInvalida, result.Status);
        Assert.Contains(ProblemaDeSenha.Longa, result.Problemas);
    }

    [Fact]
    public async Task An_address_taken_elsewhere_on_the_platform_is_reported_only_to_the_holder_of_the_token()
    {
        var servico = Criar();
        await servico.ConvidarAsync(_request, "nova@exemplo.com", "Nova", null, null);
        _contas.EmailsDaPlataforma.Add("nova@exemplo.com"); // another organization got there first

        var result = await servico.AceitarAsync(TokenDoEmail(_email), Senha);

        Assert.Equal(StatusAceite.EmailIndisponivel, result.Status);
    }

    [Fact]
    public async Task Revoking_a_pending_invitation_works_once_and_is_audited()
    {
        var servico = Criar();
        await servico.ConvidarAsync(_request, "nova@exemplo.com", "Nova", null, null);
        var id = _convites.Linhas.Single().Convite.Id;

        Assert.True(await servico.RevogarAsync(_request, id));
        Assert.False(await servico.RevogarAsync(_request, id));
        Assert.Contains(_audit.Entries, e => e.Action == IdentidadeAuditActions.ConviteRevogar);
    }
}
