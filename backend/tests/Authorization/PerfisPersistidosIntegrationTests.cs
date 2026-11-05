using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Flow4Tech.Permissoes.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Flow4Tech.Authorization.Tests.Fixtures;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Authorization.Tests;

/// <summary>
/// Ticket 06 (spec 5.4.3 to 5.4.9) on a real PostgreSQL with RLS and the application credential: profiles, rules, templates and overrides persisted per organization and read by the
/// engine, anti-escalation, factory profiles that cannot be edited, sessions ended by every change of rights, and a simulator that is the engine itself.
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class PerfisPersistidosIntegrationTests(PostgresFixture postgres)
{
    private static RegraDeAcesso Allow(string permission, decimal? percent = null, long? cap = null, Guid? unit = null) =>
        new(RuleEffect.Allow, permission, unit, percent, null, cap);

    private static RegraDeAcesso Deny(string permission) => new(RuleEffect.Deny, permission);

    private static readonly RegraDeAcesso[] ManagerRights =
    [
        Allow(PerfisPermissions.Gerir),
        Allow(PerfisPermissions.Atribuir),
        Allow(PerfisPermissions.Ver),
        Allow(Desconto, percent: 20m),
        Allow(Relatorio),
    ];

    /// <summary>The owner creates a profile with these rights and gives it to a new account; returns that account (a "manager" who may manage profiles).</summary>
    private static async Task<(Guid Owner, Guid Manager, Guid ProfileId)> ArrangeManagerAsync(PerfisTestEnv env)
    {
        var owner = await env.AddAccountAsync(owner: true);
        var manager = await env.AddAccountAsync(owner: false);
        var created = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Gerente", ManagerRights);
        Assert.Equal(ResultadoGestao.Ok, created.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), manager, created.Id!.Value));
        return (owner, manager, created.Id!.Value);
    }

    // ---- Persisted rules are what the engine reads ----

    [Fact]
    public async Task A_persisted_profile_decides_through_the_engine_and_nothing_else_does()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var waiter = await env.AddAccountAsync(owner: false);
        Assert.False((await env.DecideAsync(waiter, Desconto)).IsAllowed); // before: nothing

        var profile = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Garçom", [Allow(Desconto, percent: 10m)]);
        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), waiter, profile.Id!.Value);

        Assert.True((await env.DecideAsync(waiter, Desconto, context: new DecisionContext(Percent: 10m))).IsAllowed);
        Assert.False((await env.DecideAsync(waiter, Desconto, context: new DecisionContext(Percent: 10.5m))).IsAllowed);
        Assert.False((await env.DecideAsync(waiter, Cortesia)).IsAllowed);
    }

    [Fact]
    public async Task A_deny_rule_of_one_profile_beats_an_allow_of_another_and_a_unit_scope_is_kept()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var person = await env.AddAccountAsync(owner: false);
        var seller = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Vendas", [Allow(Relatorio), Allow(AjustarEstoque, unit: Unit2)]);
        var blocked = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Bloqueio", [Deny(Relatorio)]);
        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), person, seller.Id!.Value);

        Assert.True((await env.DecideAsync(person, Relatorio)).IsAllowed);
        Assert.True((await env.DecideAsync(person, AjustarEstoque, Unit2)).IsAllowed);
        Assert.False((await env.DecideAsync(person, AjustarEstoque, Unit1)).IsAllowed);

        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), person, blocked.Id!.Value);

        var decision = await env.DecideAsync(person, Relatorio);
        Assert.False(decision.IsAllowed);
        Assert.Equal("explicit-deny", decision.Reason);
    }

    [Fact]
    public async Task The_profiles_of_one_organization_are_invisible_to_another()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var profile = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Gerente", [Allow(Relatorio)]);

        // The same key asked in tenant B: that organization has no such profile, so its policy has no rule for it.
        var policyB = await env.Policies.ObterAsync(OrgB, CancellationToken.None);
        var policyA = await env.Policies.ObterAsync(OrgA, CancellationToken.None);

        Assert.DoesNotContain(policyB.Rules, r => r.Profile == ChavesDePerfil.Personalizado(profile.Id!.Value));
        Assert.Contains(policyA.Rules, r => r.Profile == ChavesDePerfil.Personalizado(profile.Id!.Value));
    }

    [Fact]
    public async Task A_persisted_rule_for_a_permission_the_catalog_no_longer_has_is_inert_not_fatal()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var id = Guid.NewGuid();
        await env.Uow.ExecuteAsync(
            PerfisTestEnv.OrgContext,
            ct => env.Store.CriarAsync(OrgA, new PerfilPersonalizado(id, "Antigo", null, false, [Allow("coisa:removida"), Allow(Relatorio)]), Now, ct));

        var policy = await env.Policies.ObterAsync(OrgA, CancellationToken.None);

        Assert.DoesNotContain(policy.Rules, r => r.Permission == "coisa:removida");
        Assert.Contains(policy.Rules, r => r.Permission == Relatorio);
        Assert.True((await env.DecideAsync(owner, Relatorio)).IsAllowed); // the host still decides
    }

    // ---- Factory profiles: not editable, only duplicable ----

    [Theory]
    [InlineData("dono")]
    [InlineData("conta")]
    public async Task A_factory_profile_cannot_be_edited(string key)
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);

        var result = await env.Gestao.SubstituirRegrasAsync(PerfisTestEnv.As(owner), key, [Allow(Relatorio)]);

        Assert.Equal(ResultadoGestao.PerfilDeFabrica, result);
        Assert.Empty(env.Audit.Attached);
    }

    [Fact]
    public async Task A_profile_that_does_not_exist_is_not_found()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);

        Assert.Equal(ResultadoGestao.NaoEncontrado, await env.Gestao.SubstituirRegrasAsync(PerfisTestEnv.As(owner), "perfil-que-nao-existe", []));
        Assert.Equal(ResultadoGestao.NaoEncontrado, await env.Gestao.SubstituirRegrasAsync(PerfisTestEnv.As(owner), Guid.NewGuid().ToString("D"), []));
        Assert.Equal(ResultadoGestao.NaoEncontrado, (await env.Gestao.DuplicarAsync(PerfisTestEnv.As(owner), "perfil-que-nao-existe", "X", acompanhar: false)).Resultado);
    }

    [Fact]
    public async Task Duplicating_a_factory_profile_by_copy_gives_an_editable_profile_with_the_same_rules()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);

        var copy = await env.Gestao.DuplicarAsync(PerfisTestEnv.As(owner), "conta", "Conta do salão", acompanhar: false);

        Assert.Equal(ResultadoGestao.Ok, copy.Resultado);
        var stored = await env.Uow.ExecuteAsync(PerfisTestEnv.OrgContext, ct => env.Store.ObterAsync(OrgA, copy.Id!.Value, ct));
        Assert.NotNull(stored);
        Assert.False(stored.AcompanhaModelo);
        Assert.Equal(6, stored.Regras.Count); // the factory "conta" rules: 5 of ticket 05 + conta:gerir-google (ticket 09)
        Assert.Equal(ResultadoGestao.Ok, await env.Gestao.SubstituirRegrasAsync(PerfisTestEnv.As(owner), copy.Id!.Value.ToString("D"), [Allow(Relatorio)])); // editable
    }

    [Fact]
    public async Task Following_a_template_keeps_the_template_rules_in_force_and_copying_does_not()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var follower = await env.AddAccountAsync(owner: false);
        var copier = await env.AddAccountAsync(owner: false);
        var template = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Modelo", [Allow(Relatorio)]);
        var templateKey = template.Id!.Value.ToString("D");
        var follows = await env.Gestao.DuplicarAsync(PerfisTestEnv.As(owner), templateKey, "Segue", acompanhar: true);
        var copies = await env.Gestao.DuplicarAsync(PerfisTestEnv.As(owner), templateKey, "Copia", acompanhar: false);
        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), follower, follows.Id!.Value);
        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), copier, copies.Id!.Value);
        Assert.True((await env.DecideAsync(follower, Relatorio)).IsAllowed);
        Assert.True((await env.DecideAsync(copier, Relatorio)).IsAllowed);

        // The template moves: the follower moves with it, the copy does not.
        await env.Gestao.SubstituirRegrasAsync(PerfisTestEnv.As(owner), templateKey, [Allow(FecharCaixa)]);

        Assert.False((await env.DecideAsync(follower, Relatorio)).IsAllowed);
        Assert.True((await env.DecideAsync(follower, FecharCaixa)).IsAllowed);
        Assert.True((await env.DecideAsync(copier, Relatorio)).IsAllowed);
        Assert.False((await env.DecideAsync(copier, FecharCaixa)).IsAllowed);
    }

    // ---- Anti-escalation ----

    [Fact]
    public async Task Nobody_changes_their_own_rights_by_assignment_or_override()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var (owner, manager, _) = await ArrangeManagerAsync(env);
        var extra = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Extra", [Allow(Relatorio)]);

        Assert.Equal(ResultadoGestao.AutoPromocao, await env.Gestao.AtribuirAsync(PerfisTestEnv.As(manager), manager, extra.Id!.Value));
        Assert.Equal(ResultadoGestao.AutoPromocao, await env.Gestao.RemoverAtribuicaoAsync(PerfisTestEnv.As(manager), manager, extra.Id.Value));
        Assert.Equal(ResultadoGestao.AutoPromocao, await env.Gestao.DefinirOverridesAsync(PerfisTestEnv.As(manager), manager, [Allow(Relatorio)]));
        // The owner is not special either.
        Assert.Equal(ResultadoGestao.AutoPromocao, await env.Gestao.DefinirOverridesAsync(PerfisTestEnv.As(owner), owner, [Allow(Relatorio)]));
        Assert.Contains(env.Audit.Detached, e => e.Action == PerfisAuditActions.PermissaoNegarEscalada && e.Result == AuditResult.Negado);
    }

    [Fact]
    public async Task Nobody_edits_a_profile_they_hold()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var (owner, manager, managerProfile) = await ArrangeManagerAsync(env);

        var result = await env.Gestao.SubstituirRegrasAsync(PerfisTestEnv.As(manager), managerProfile.ToString("D"), [.. ManagerRights, Allow(FecharCaixa)]);

        Assert.Equal(ResultadoGestao.AutoPromocao, result);
        Assert.False((await env.DecideAsync(manager, FecharCaixa)).IsAllowed);
        // Someone else may (here the owner), within what they hold.
        Assert.Equal(ResultadoGestao.Ok, await env.Gestao.SubstituirRegrasAsync(PerfisTestEnv.As(owner), managerProfile.ToString("D"), [.. ManagerRights, Allow(FecharCaixa)]));
        Assert.True((await env.DecideAsync(manager, FecharCaixa)).IsAllowed);
    }

    [Fact]
    public async Task A_manager_cannot_hand_on_what_they_do_not_hold_nor_a_limit_they_do_not_have()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var (_, manager, _) = await ArrangeManagerAsync(env);
        var target = await env.AddAccountAsync(owner: false);

        // Not held at all.
        Assert.Equal(ResultadoGestao.EscalacaoNegada, (await env.Gestao.CriarAsync(PerfisTestEnv.As(manager), "Caixa", [Allow(FecharCaixa)])).Resultado);
        // Held up to 20%: more, or no limit, is a raise.
        Assert.Equal(ResultadoGestao.EscalacaoNegada, (await env.Gestao.CriarAsync(PerfisTestEnv.As(manager), "Desc30", [Allow(Desconto, percent: 30m)])).Resultado);
        Assert.Equal(ResultadoGestao.EscalacaoNegada, (await env.Gestao.CriarAsync(PerfisTestEnv.As(manager), "DescLivre", [Allow(Desconto)])).Resultado);
        Assert.Equal(ResultadoGestao.EscalacaoNegada, await env.Gestao.DefinirOverridesAsync(PerfisTestEnv.As(manager), target, [Allow(Cortesia)]));
        // Within what they hold: fine, and a deny always is.
        var ok = await env.Gestao.CriarAsync(PerfisTestEnv.As(manager), "Desc15", [Allow(Desconto, percent: 15m), Allow(Relatorio), Deny(Cortesia)]);
        Assert.Equal(ResultadoGestao.Ok, ok.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await env.Gestao.AtribuirAsync(PerfisTestEnv.As(manager), target, ok.Id!.Value));
        Assert.True((await env.DecideAsync(target, Desconto, context: new DecisionContext(Percent: 15m))).IsAllowed);
        Assert.False((await env.DecideAsync(target, Desconto, context: new DecisionContext(Percent: 16m))).IsAllowed);
    }

    [Fact]
    public async Task Assigning_a_profile_is_refused_when_it_carries_more_than_the_assigner_holds_including_the_template()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var (owner, manager, _) = await ArrangeManagerAsync(env);
        var target = await env.AddAccountAsync(owner: false);
        var template = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Modelo caixa", [Allow(FecharCaixa)]);
        // The follower has no rules of its own: what it carries comes entirely from the template.
        var follower = await env.Gestao.DuplicarAsync(PerfisTestEnv.As(owner), template.Id!.Value.ToString("D"), "Segue caixa", acompanhar: true);

        Assert.Equal(ResultadoGestao.EscalacaoNegada, await env.Gestao.AtribuirAsync(PerfisTestEnv.As(manager), target, follower.Id!.Value));
        Assert.Equal(ResultadoGestao.EscalacaoNegada, await env.Gestao.AtribuirAsync(PerfisTestEnv.As(manager), target, template.Id.Value));
        Assert.False((await env.DecideAsync(target, FecharCaixa)).IsAllowed);
    }

    [Fact]
    public async Task Without_the_management_permission_nothing_is_done()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var plain = await env.AddAccountAsync(owner: false);
        var other = await env.AddAccountAsync(owner: false);
        var profile = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Qualquer", [Allow(Relatorio)]);

        Assert.Equal(ResultadoGestao.Negado, (await env.Gestao.CriarAsync(PerfisTestEnv.As(plain), "Meu", [])).Resultado);
        Assert.Equal(ResultadoGestao.Negado, await env.Gestao.AtribuirAsync(PerfisTestEnv.As(plain), other, profile.Id!.Value));
        Assert.Equal(ResultadoGestao.Negado, await env.Gestao.DefinirOverridesAsync(PerfisTestEnv.As(plain), other, []));
        Assert.Equal(ResultadoGestao.Negado, await env.Gestao.SubstituirRegrasAsync(PerfisTestEnv.As(plain), profile.Id.Value.ToString("D"), []));
        Assert.False((await env.DecideAsync(other, Relatorio)).IsAllowed);
    }

    [Fact]
    public async Task Invalid_input_is_refused_and_a_duplicate_name_too()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);

        Assert.Equal(ResultadoGestao.Invalido, (await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "  ", [])).Resultado);
        Assert.Equal(ResultadoGestao.Invalido, (await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), new string('x', 81), [])).Resultado);
        Assert.Equal(ResultadoGestao.Invalido, (await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "P", [Allow("coisa:inexistente")])).Resultado);
        Assert.Equal(ResultadoGestao.Invalido, (await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "P", [new RegraDeAcesso(RuleEffect.Deny, Relatorio, MaxPercent: 5m)])).Resultado);
        Assert.Equal(ResultadoGestao.Invalido, (await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "P", [Allow(Desconto, percent: 101m)])).Resultado);
        Assert.Equal(ResultadoGestao.Ok, (await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Unico", [])).Resultado);
        Assert.Equal(ResultadoGestao.NomeEmUso, (await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), " Unico ", [])).Resultado);
    }

    [Fact]
    public async Task A_rule_for_a_unit_that_does_not_exist_here_is_invalid_not_a_server_error()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var person = await env.AddAccountAsync(owner: false);
        var created = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Com unidade", [Allow(Relatorio, unit: Unit2)]);
        Assert.Equal(ResultadoGestao.Ok, created.Resultado);

        var unknown = Guid.NewGuid();
        var otherOrganizationUnit = UnitB; // exists, but in tenant B: RLS hides it from tenant A
        foreach (var unit in new[] { unknown, otherOrganizationUnit })
        {
            Assert.Equal(ResultadoGestao.Invalido, (await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "P" + unit, [Allow(Relatorio, unit: unit)])).Resultado);
            Assert.Equal(ResultadoGestao.Invalido, await env.Gestao.SubstituirRegrasAsync(PerfisTestEnv.As(owner), created.Id!.Value.ToString("D"), [Allow(Relatorio, unit: unit)]));
            Assert.Equal(ResultadoGestao.Invalido, await env.Gestao.DefinirOverridesAsync(PerfisTestEnv.As(owner), person, [Allow(Relatorio, unit: unit)]));
        }

        // The refused attempts changed nothing: the profile keeps its rule for the real unit.
        var policy = await env.Policies.ObterAsync(OrgA, CancellationToken.None);
        Assert.Contains(policy.Rules, r => r.Profile == ChavesDePerfil.Personalizado(created.Id!.Value) && r.UnitId == Unit2);
    }

    // ---- Sessions are ended by every change of rights (spec 5.4.9) ----

    [Fact]
    public async Task Every_change_of_rights_ends_the_sessions_of_the_affected_account_only()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var holder = await env.AddAccountAsync(owner: false);
        var follower = await env.AddAccountAsync(owner: false);
        var bystander = await env.AddAccountAsync(owner: false);
        var profile = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Base", [Allow(Relatorio)]);
        var follows = await env.Gestao.DuplicarAsync(PerfisTestEnv.As(owner), profile.Id!.Value.ToString("D"), "Segue base", acompanhar: true);
        var versionOf = async () => (await env.CredentialVersionAsync(holder), await env.CredentialVersionAsync(follower), await env.CredentialVersionAsync(bystander));

        var start = await versionOf();
        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), holder, profile.Id.Value);
        var afterAssign = await versionOf();
        Assert.True(afterAssign.Item1 > start.Item1); // assigning ends the holder's sessions
        Assert.Equal((start.Item2, start.Item3), (afterAssign.Item2, afterAssign.Item3));

        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), follower, follows.Id!.Value);
        var beforeEdit = await versionOf();
        await env.Gestao.SubstituirRegrasAsync(PerfisTestEnv.As(owner), profile.Id.Value.ToString("D"), [Allow(Relatorio), Allow(FecharCaixa)]);
        var afterEdit = await versionOf();
        Assert.True(afterEdit.Item1 > beforeEdit.Item1); // a holder of the edited profile
        Assert.True(afterEdit.Item2 > beforeEdit.Item2); // a holder of a profile that FOLLOWS it
        Assert.Equal(beforeEdit.Item3, afterEdit.Item3); // somebody who holds nothing is untouched

        await env.Gestao.DefinirOverridesAsync(PerfisTestEnv.As(owner), bystander, [Deny(Relatorio)]);
        var afterOverride = await versionOf();
        Assert.True(afterOverride.Item3 > afterEdit.Item3);
        Assert.Equal((afterEdit.Item1, afterEdit.Item2), (afterOverride.Item1, afterOverride.Item2));

        await env.Gestao.RemoverAtribuicaoAsync(PerfisTestEnv.As(owner), holder, profile.Id.Value);
        var afterRemove = await versionOf();
        Assert.True(afterRemove.Item1 > afterOverride.Item1);
    }

    [Fact]
    public async Task A_refused_change_ends_nothing_and_leaves_nothing_behind()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var (_, manager, _) = await ArrangeManagerAsync(env);
        var target = await env.AddAccountAsync(owner: false);
        var before = await env.CredentialVersionAsync(target);
        var recorded = env.Audit.Attached.Count;

        Assert.Equal(ResultadoGestao.EscalacaoNegada, await env.Gestao.DefinirOverridesAsync(PerfisTestEnv.As(manager), target, [Allow(Relatorio), Allow(FecharCaixa)]));

        Assert.Equal(before, await env.CredentialVersionAsync(target));
        Assert.False((await env.DecideAsync(target, Relatorio)).IsAllowed); // not even the part the manager could have given
        Assert.Equal(recorded, env.Audit.Attached.Count);
    }

    [Fact]
    public async Task Changes_are_audited_in_the_same_unit_of_work()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var person = await env.AddAccountAsync(owner: false);

        var profile = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Auditado", [Allow(Relatorio)]);
        await env.Gestao.SubstituirRegrasAsync(PerfisTestEnv.As(owner), profile.Id!.Value.ToString("D"), [Allow(Relatorio), Allow(FecharCaixa)]);
        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), person, profile.Id.Value);
        await env.Gestao.DefinirOverridesAsync(PerfisTestEnv.As(owner), person, [Deny(Cortesia)]);

        Assert.Equal(
            [PerfisAuditActions.PerfilCriar, PerfisAuditActions.PerfilEditarRegras, PerfisAuditActions.PerfilAtribuir, PerfisAuditActions.PermissaoDefinirOverrides],
            env.Audit.Attached.Select(e => e.Action));
    }

    // ---- Simulator ----

    [Fact]
    public async Task The_simulator_returns_exactly_what_the_engine_decides()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var (owner, manager, _) = await ArrangeManagerAsync(env);
        var target = await env.AddAccountAsync(owner: false);
        var profile = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Alvo", [Allow(Desconto, percent: 10m), Allow(AjustarEstoque, unit: Unit2), Deny(Cortesia)]);
        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), target, profile.Id!.Value);

        foreach (var (permission, unit, percent) in new (string, Guid?, decimal?)[]
        {
            (Desconto, Unit1, 5m), (Desconto, Unit1, 11m), (Desconto, null, null), (AjustarEstoque, Unit2, null), (AjustarEstoque, Unit1, null),
            (Cortesia, Unit1, null), (Relatorio, Unit1, null), ("coisa:inexistente", Unit1, null),
        })
        {
            var context = new DecisionContext(Percent: percent);
            var simulated = await env.Simulador.SimularAsync(PerfisTestEnv.As(manager), target, permission, unit, context);
            var real = await env.DecideAsync(target, permission, unit, context);

            Assert.True(simulated.Autorizado);
            Assert.Equal(real, simulated.Decisao);
        }
    }

    [Fact]
    public async Task The_simulator_reserves_nothing_while_a_real_decision_does()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var waiter = await env.AddAccountAsync(owner: false);
        var profile = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "Com teto", [Allow(Cortesia, cap: 5_000)]);
        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), waiter, profile.Id!.Value);
        var spend = new DecisionContext(new Money(3_000, "BRL"));

        var first = await env.Simulador.SimularAsync(PerfisTestEnv.As(owner), waiter, Cortesia, Unit1, spend);
        var second = await env.Simulador.SimularAsync(PerfisTestEnv.As(owner), waiter, Cortesia, Unit1, spend);

        Assert.True(first.Decisao!.IsAllowed);
        Assert.True(second.Decisao!.IsAllowed); // asking twice is not spending twice
        Assert.Equal(0, await env.ConsumedAsync(waiter, Cortesia));

        var real = await env.Uow.ExecuteAsync(
            PerfisTestEnv.OrgContext,
            async ct => await env.Engine.DecidirAsync(new Subject(waiter, await env.KeysOfAsync(waiter)), Cortesia, new Scope(OrgA, Unit1), spend, ct));

        Assert.True(real.IsAllowed);
        Assert.Equal(3_000, await env.ConsumedAsync(waiter, Cortesia));
    }

    [Fact]
    public async Task The_simulator_needs_permission_to_view_profiles_or_to_assign_them()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);
        var plain = await env.AddAccountAsync(owner: false);
        var viewer = await env.AddAccountAsync(owner: false);
        var viewerProfile = await env.Gestao.CriarAsync(PerfisTestEnv.As(owner), "So ver", [Allow(PerfisPermissions.Ver)]);
        await env.Gestao.AtribuirAsync(PerfisTestEnv.As(owner), viewer, viewerProfile.Id!.Value);

        var denied = await env.Simulador.SimularAsync(PerfisTestEnv.As(plain), owner, Relatorio, Unit1, new DecisionContext());
        var allowed = await env.Simulador.SimularAsync(PerfisTestEnv.As(viewer), owner, Relatorio, Unit1, new DecisionContext());

        Assert.False(denied.Autorizado);
        Assert.Null(denied.Decisao);
        Assert.True(allowed.Autorizado);
        Assert.True(allowed.Decisao!.IsAllowed);
    }

    [Fact]
    public async Task The_simulator_denies_for_a_person_who_does_not_exist()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);
        var owner = await env.AddAccountAsync(owner: true);

        var result = await env.Simulador.SimularAsync(PerfisTestEnv.As(owner), Guid.NewGuid(), Relatorio, Unit1, new DecisionContext());

        Assert.True(result.Autorizado);
        Assert.False(result.Decisao!.IsAllowed);
    }

    // ---- Schema ----

    [Fact]
    public async Task The_model_matches_the_migrated_schema()
    {
        await using var env = await PerfisTestEnv.CreateAsync(postgres);

        await using var tx = await env.Tenant.BeginAsync(PerfisTestEnv.OrgContext);
        await using var context = new PermissoesDbContext(tx);

        Assert.Empty(await SchemaDrift.FindAsync(context));
    }
}
