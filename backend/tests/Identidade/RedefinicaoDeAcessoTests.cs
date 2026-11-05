using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Tests;

internal sealed class FixedCredentialGenerator : ITemporaryCredentialGenerator
{
    public int Count { get; private set; }

    public string Generate() => "TEMP-" + ++Count + "-abcdefghij";
}

/// <summary>Spec 5.2.5 (ticket 09): whoever holds <c>usuario:redefinir-acesso</c> generates a NEW temporary credential for an operational account; nobody types it for them. No database.</summary>
public sealed class RedefinicaoDeAcessoTests
{
    private static readonly Guid Org = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    private readonly FixedClock _clock = new();
    private readonly FakeContas _contas = new();
    private readonly RecordingAudit _audit = new();
    private readonly FixedCredentialGenerator _generator = new();
    private readonly Conta _gerente;

    public RedefinicaoDeAcessoTests() => _gerente = _contas.Adicionar(Org, TipoConta.Email, "x", "gerente@a.com");

    private GestaoDeContas Criar() =>
        new(new FakeUnitOfWork(), _contas, new FakeHasher(), new FakeCompromised(), _generator, _audit, _clock, new ValidadeDaCredencialTemporaria(TimeSpan.FromHours(72)));

    private static FakeRequest Como(Conta ator) => new(ator.Id, new OrganizationContext(Org, Guid.NewGuid()));

    [Fact]
    public async Task It_gives_a_new_temporary_credential_once_with_a_deadline_and_ends_the_old_sessions()
    {
        var garcom = _contas.Adicionar(Org, TipoConta.Operacional, "senha-antiga-123", usuario: "joao");

        var result = await Criar().RedefinirAcessoAsync(Como(_gerente), garcom.Id);

        Assert.Equal(StatusRedefinicao.Redefinida, result.Status);
        Assert.Equal("TEMP-1-abcdefghij", result.Credencial!.Valor);
        Assert.Equal(_clock.UtcNow + TimeSpan.FromHours(72), result.ExpiraEm);
        var linha = _contas.Linhas[garcom.Id];
        Assert.Equal("hash:TEMP-1-abcdefghij", linha.Hash);
        Assert.True(linha.DeveTrocar);
        Assert.Equal(_clock.UtcNow + TimeSpan.FromHours(72), linha.Expira);
        Assert.Equal(2, linha.Conta.VersaoCredencial);
        var entrada = Assert.Single(_audit.Entries, e => e.Action == IdentidadeAuditActions.ContaRedefinirAcesso);
        Assert.Equal(AuditResult.Sucesso, entrada.Result);
    }

    [Fact]
    public async Task A_credential_past_its_deadline_is_replaced_by_a_fresh_one()
    {
        var garcom = _contas.Adicionar(Org, TipoConta.Operacional, "x", usuario: "joao", deveTrocar: true);
        var servico = Criar();
        await servico.RedefinirAcessoAsync(Como(_gerente), garcom.Id);
        _clock.UtcNow += TimeSpan.FromHours(100);

        var segunda = await servico.RedefinirAcessoAsync(Como(_gerente), garcom.Id);

        Assert.Equal(StatusRedefinicao.Redefinida, segunda.Status);
        Assert.Equal(_clock.UtcNow + TimeSpan.FromHours(72), _contas.Linhas[garcom.Id].Expira);
    }

    [Fact]
    public async Task Accounts_with_e_mail_use_recovery_not_this()
    {
        var result = await Criar().RedefinirAcessoAsync(Como(_gerente), _contas.Adicionar(Org, TipoConta.Email, "x", "outra@a.com").Id);

        Assert.Equal(StatusRedefinicao.ContaComEmail, result.Status);
        Assert.Null(result.Credencial);
    }

    [Fact]
    public async Task Nobody_resets_their_own_access_or_a_missing_or_inactive_account()
    {
        var operacionalAtor = _contas.Adicionar(Org, TipoConta.Operacional, "x", usuario: "ana");
        var inativa = _contas.Adicionar(Org, TipoConta.Operacional, "x", usuario: "velho", ativa: false);

        Assert.Equal(StatusRedefinicao.Negada, (await Criar().RedefinirAcessoAsync(Como(operacionalAtor), operacionalAtor.Id)).Status);
        Assert.Equal(StatusRedefinicao.NaoEncontrada, (await Criar().RedefinirAcessoAsync(Como(_gerente), Guid.NewGuid())).Status);
        Assert.Equal(StatusRedefinicao.NaoEncontrada, (await Criar().RedefinirAcessoAsync(Como(_gerente), inativa.Id)).Status);
    }

    [Fact]
    public async Task An_actor_who_must_still_change_the_password_or_is_inactive_cannot_reset_and_the_denial_is_audited()
    {
        var garcom = _contas.Adicionar(Org, TipoConta.Operacional, "x", usuario: "joao");
        var temporario = _contas.Adicionar(Org, TipoConta.Email, "x", "t@a.com", deveTrocar: true);
        var inativo = _contas.Adicionar(Org, TipoConta.Email, "x", "i@a.com", ativa: false);

        foreach (var ator in new[] { temporario, inativo })
        {
            Assert.Equal(StatusRedefinicao.Negada, (await Criar().RedefinirAcessoAsync(Como(ator), garcom.Id)).Status);
        }

        Assert.Equal("hash:x", _contas.Linhas[garcom.Id].Hash);
        Assert.Equal(2, _audit.Entries.Count(e => e.Action == IdentidadeAuditActions.ContaRedefinirAcesso && e.Result == AuditResult.Negado));
    }
}
