using Flow4Tech.Identidade.Domain;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Spec 5.2.7: owner not deactivated or demoted by another, nobody changes their own permissions, always at least one owner.</summary>
public sealed class ProtecaoDeContaTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static Conta Dono(bool ativa = true) => new(Guid.NewGuid(), Org, TipoConta.Email, "Dono", "dono@bar.com", null, Dono: true, ativa, 1);

    private static Conta Gerente() => new(Guid.NewGuid(), Org, TipoConta.Email, "Gerente", "gerente@bar.com", null, Dono: false, Ativa: true, 1);

    private static Conta Garcom() => new(Guid.NewGuid(), Org, TipoConta.Operacional, "Garcom", null, "garcom", Dono: false, Ativa: true, 1);

    [Fact]
    public void An_owner_cannot_be_deactivated_by_someone_else_not_even_another_owner()
    {
        var alvo = Dono();
        Assert.Equal(RecusaDeProtecao.DonoNaoPodeSerDesativadoPorOutro, ProtecaoDeConta.PodeDesativar(Dono(), alvo, donosAtivos: 2));
        Assert.Equal(RecusaDeProtecao.DonoNaoPodeSerDesativadoPorOutro, ProtecaoDeConta.PodeDesativar(Gerente(), alvo, donosAtivos: 2));
    }

    [Fact]
    public void The_last_owner_cannot_deactivate_themselves_but_one_of_two_can()
    {
        var dono = Dono();
        Assert.Equal(RecusaDeProtecao.UltimoDono, ProtecaoDeConta.PodeDesativar(dono, dono, donosAtivos: 1));
        Assert.Null(ProtecaoDeConta.PodeDesativar(dono, dono, donosAtivos: 2));
    }

    [Fact]
    public void Ordinary_accounts_can_be_deactivated_and_missing_or_inactive_ones_are_refused()
    {
        var ator = Dono();
        Assert.Null(ProtecaoDeConta.PodeDesativar(ator, Garcom(), donosAtivos: 1));
        Assert.Equal(RecusaDeProtecao.ContaInexistente, ProtecaoDeConta.PodeDesativar(ator, null, 1));
        Assert.Equal(RecusaDeProtecao.ContaInexistente, ProtecaoDeConta.PodeDesativar(null, Garcom(), 1));
        Assert.Equal(RecusaDeProtecao.ContaDesativada, ProtecaoDeConta.PodeDesativar(ator, Garcom() with { Ativa = false }, 1));
        Assert.Equal(RecusaDeProtecao.AtorDesativado, ProtecaoDeConta.PodeDesativar(Dono(ativa: false), Garcom(), 1));
    }

    [Fact]
    public void Nobody_changes_their_own_permissions_in_either_direction()
    {
        var dono = Dono();
        var gerente = Gerente();
        Assert.Equal(RecusaDeProtecao.AutoAlteracaoDePermissao, ProtecaoDeConta.PodeDefinirDono(dono, dono, tornarDono: false, donosAtivos: 2));
        Assert.Equal(RecusaDeProtecao.AutoAlteracaoDePermissao, ProtecaoDeConta.PodeDefinirDono(gerente, gerente, tornarDono: true, donosAtivos: 1));
    }

    [Fact]
    public void Only_an_owner_names_another_owner_and_only_an_account_with_email_can_be_one()
    {
        Assert.Equal(RecusaDeProtecao.ApenasDonoAlteraDono, ProtecaoDeConta.PodeDefinirDono(Gerente(), Gerente(), true, 1));
        Assert.Equal(RecusaDeProtecao.DonoPrecisaDeEmail, ProtecaoDeConta.PodeDefinirDono(Dono(), Garcom(), true, 1));
        Assert.Null(ProtecaoDeConta.PodeDefinirDono(Dono(), Gerente(), true, 1));
    }

    [Fact]
    public void An_owner_is_never_demoted_by_another_even_when_two_owners_exist()
    {
        Assert.Equal(RecusaDeProtecao.DonoNaoPodeSerRebaixado, ProtecaoDeConta.PodeDefinirDono(Dono(), Dono(), tornarDono: false, donosAtivos: 2));
    }

    [Fact]
    public void Naming_an_existing_owner_or_demoting_a_non_owner_changes_nothing()
    {
        Assert.Equal(RecusaDeProtecao.SemMudanca, ProtecaoDeConta.PodeDefinirDono(Dono(), Dono(), tornarDono: true, donosAtivos: 2));
        Assert.Equal(RecusaDeProtecao.SemMudanca, ProtecaoDeConta.PodeDefinirDono(Dono(), Gerente(), tornarDono: false, donosAtivos: 1));
    }

    [Fact]
    public void An_inactive_target_or_actor_cannot_be_part_of_an_owner_change()
    {
        Assert.Equal(RecusaDeProtecao.ContaDesativada, ProtecaoDeConta.PodeDefinirDono(Dono(), Gerente() with { Ativa = false }, true, 1));
        Assert.Equal(RecusaDeProtecao.AtorDesativado, ProtecaoDeConta.PodeDefinirDono(Dono(ativa: false), Gerente(), true, 1));
        Assert.Equal(RecusaDeProtecao.ContaInexistente, ProtecaoDeConta.PodeDefinirDono(Dono(), null, true, 1));
    }
}
