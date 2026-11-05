using System.Text.Json;
using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Infrastructure.Outbox;
using Flow4Tech.Isolation.Tests;
using Microsoft.Extensions.DependencyInjection;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Identidade.Tests;

/// <summary>
/// Spec 5.5.5 and 5.5.6 against real PostgreSQL: the new-login detection (device and network learned per account), the security events in the outbox on the unit of work of the action,
/// and the e-mail alerts to the person and to the owners through the dispatcher and the development sender.
/// </summary>
[Collection(PostgresDefinition.Name)]
public sealed class AlertasDeSegurancaIntegrationTests(PostgresFixture postgres)
{
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string Firefox = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0";
    private const string Casa = "203.0.113.20";
    private const string Bar = "198.51.100.40";

    private static ClienteDaSessao Cliente(string ip, string agente) => new(ip, agente);

    private static Task<SessaoEmitida> EntrarAsync(IdentidadeEnv env, Guid conta, string ip, string agente) =>
        env.Sessoes.IniciarAsync(conta, OrgA, 1, false, Cliente(ip, agente));

    private static Task<long> EventosAsync(IdentidadeEnv env, string tipo) =>
        env.CountAsync(OrgA, $"SELECT count(*) FROM outbox_eventos WHERE tipo = '{tipo}'");

    private static async Task<IReadOnlyList<MensagemDeEmail>> EntregarAsync(IdentidadeEnv env)
    {
        var dispatcher = new OutboxDispatcher(env.Tenant, env.Get<IEnumerable<IOutboxConsumer>>(), env.Clock);
        await dispatcher.DispatchAsync(IdentidadeEnv.Pre(OrgA));
        return env.Get<EnviadorDeEmailDeDesenvolvimento>().Enviadas;
    }

    [Fact]
    public async Task The_first_login_only_sets_the_baseline_and_raises_no_alert()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");

        await EntrarAsync(env, dono, Casa, Chrome);

        Assert.Equal(0, await EventosAsync(env, LoginNovo.Type));
        Assert.Equal(2, await env.CountAsync(OrgA, "SELECT count(*) FROM acessos_conhecidos")); // one device, one network
    }

    [Fact]
    public async Task The_same_device_and_network_again_is_not_new_even_after_a_browser_update()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        await EntrarAsync(env, dono, Casa, Chrome);

        await EntrarAsync(env, dono, "203.0.113.99", Chrome.Replace("120.0.0.0", "121.0.6167.85", StringComparison.Ordinal));

        Assert.Equal(0, await EventosAsync(env, LoginNovo.Type));
        Assert.Equal(2, await env.CountAsync(OrgA, "SELECT count(*) FROM acessos_conhecidos"));
    }

    [Fact]
    public async Task A_new_device_raises_the_event_and_so_does_a_new_network_each_only_once()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        await EntrarAsync(env, dono, Casa, Chrome);

        var sessaoFirefox = await EntrarAsync(env, dono, Casa, Firefox); // new device, same network
        var sessaoBar = await EntrarAsync(env, dono, Bar, Chrome); // same device, new network
        await EntrarAsync(env, dono, Bar, Firefox); // both already known now

        var eventos = (await IdentidadeEnv.QueryAsync(env.App, OrgA, "SELECT payload::text FROM outbox_eventos WHERE tipo = 'seguranca.login-novo' ORDER BY ocorrido_em, id", r => r.GetString(0)))
            .Select(json => JsonDocument.Parse(json).RootElement).ToList();
        Assert.Equal(2, eventos.Count);
        var firefox = eventos.Single(e => e.GetProperty("SessaoId").GetGuid() == sessaoFirefox.SessaoId);
        Assert.True(firefox.GetProperty("AparelhoNovo").GetBoolean());
        Assert.False(firefox.GetProperty("RedeNova").GetBoolean());
        var bar = eventos.Single(e => e.GetProperty("SessaoId").GetGuid() == sessaoBar.SessaoId);
        Assert.False(bar.GetProperty("AparelhoNovo").GetBoolean());
        Assert.True(bar.GetProperty("RedeNova").GetBoolean());
        Assert.Equal("198.51.100.0/24", bar.GetProperty("Rede").GetString());
        Assert.DoesNotContain(Bar, string.Concat(eventos.Select(e => e.GetRawText())), StringComparison.Ordinal); // the network prefix, never the address
    }

    [Fact]
    public async Task The_new_session_after_a_password_change_is_neither_judged_nor_learned()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        await EntrarAsync(env, dono, Casa, Chrome);

        await env.Sessoes.IniciarAposTrocaDeSenhaAsync(OrgA, dono, Cliente(Bar, Firefox));

        Assert.Equal(0, await EventosAsync(env, LoginNovo.Type));
        Assert.Equal(2, await env.CountAsync(OrgA, "SELECT count(*) FROM acessos_conhecidos"));
    }

    [Fact]
    public async Task Only_the_latest_fifty_values_per_kind_are_kept()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");

        for (var i = 0; i < 60; i++)
        {
            env.Clock.UtcNow += TimeSpan.FromMinutes(1);
            await EntrarAsync(env, dono, $"203.0.{i}.5", $"Navegador-{(char)('a' + (i % 26))}{(char)('a' + (i / 26))} (teste)");
        }

        Assert.Equal(50, await env.CountAsync(OrgA, "SELECT count(*) FROM acessos_conhecidos WHERE tipo = 'rede'"));
        Assert.True(await env.CountAsync(OrgA, "SELECT count(*) FROM acessos_conhecidos WHERE tipo = 'aparelho'") <= 50);
    }

    [Fact]
    public async Task A_new_login_of_an_account_with_email_alerts_the_person_and_the_owner_once_each()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");
        await EntrarAsync(env, gerente, Casa, Chrome);
        await EntrarAsync(env, gerente, Bar, Firefox);

        var mensagens = await EntregarAsync(env);
        _ = await EntregarAsync(env); // delivered once: a second run finds nothing pending

        Assert.Equal(["dono@a.com", "gerente@a.com"], mensagens.Select(m => m.Para).Order().ToArray());
        Assert.All(mensagens, m => Assert.Equal("Novo acesso à sua conta Flow4Tech", m.Assunto));
        Assert.Contains("troque a senha", mensagens.Single(m => m.Para == "gerente@a.com").Texto, StringComparison.Ordinal);
        Assert.Contains("Gerente gerente@a.com", mensagens.Single(m => m.Para == "dono@a.com").Texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_owner_is_the_one_who_logs_in_from_a_new_place_there_is_one_message_not_two()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        await EntrarAsync(env, dono, Casa, Chrome);
        await EntrarAsync(env, dono, Bar, Chrome);

        var mensagens = await EntregarAsync(env);

        Assert.Equal("dono@a.com", Assert.Single(mensagens).Para);
    }

    [Fact]
    public async Task A_new_login_of_an_operational_account_alerts_only_the_owners_because_it_has_no_email()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var dono = await env.CriarDonoAsync(OrgA, "dono@a.com");
        var (maria, _) = await env.CriarOperacionalAsync(dono, OrgA, "maria");
        await EntrarAsync(env, maria, Casa, Chrome);
        await EntrarAsync(env, maria, Bar, Firefox);

        var mensagens = await EntregarAsync(env);

        Assert.Equal("dono@a.com", Assert.Single(mensagens).Para);
        Assert.Contains("Operacional maria", mensagens[0].Texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_password_raises_the_security_event_and_tells_the_person()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgA, "dono@a.com");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");

        var troca = await env.Troca.TrocarAsync(OrgA, gerente, IdentidadeEnv.Senha, "outra-senha-bem-longa-2026");

        Assert.Equal(StatusTrocaDeSenha.Trocada, troca.Status);
        Assert.Equal(1, await EventosAsync(env, SenhaTrocada.Type));
        var mensagens = await EntregarAsync(env);
        var mensagem = Assert.Single(mensagens);
        Assert.Equal("gerente@a.com", mensagem.Para);
        Assert.DoesNotContain("outra-senha", mensagem.Texto, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_password_change_raises_no_event()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        await env.CriarDonoAsync(OrgA, "dono@a.com");
        var gerente = await env.SeedGerenteAsync(OrgA, "gerente@a.com");

        var troca = await env.Troca.TrocarAsync(OrgA, gerente, "senha-atual-errada-123", "outra-senha-bem-longa-2026");

        Assert.Equal(StatusTrocaDeSenha.SenhaAtualInvalida, troca.Status);
        Assert.Equal(0, await EventosAsync(env, SenhaTrocada.Type));
    }

    [Fact]
    public async Task A_lock_by_failures_is_audited_raises_the_event_and_tells_the_person_and_the_owner()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres, tweak: o => o.Bloqueio = new LimitesDeLogin { FalhasParaBloquearEmail = 3, FalhasParaCaptchaConta = 100 });
        await env.CriarDonoAsync(OrgA, "dono@a.com");
        await env.SeedGerenteAsync(OrgA, "gerente@a.com");
        var contexto = new ContextoDeLogin(Casa, null);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(new FalhaDeAutenticacao(), await env.Auth.AutenticarPorEmailAsync("gerente@a.com", "senha-errada-123456", contexto));
        }

        Assert.IsType<LoginBloqueado>(await env.Auth.AutenticarPorEmailAsync("gerente@a.com", IdentidadeEnv.Senha, contexto)); // the right password is refused too
        Assert.Equal(1, await EventosAsync(env, ContaBloqueada.Type));
        Assert.Equal(1, await env.CountAsync(OrgA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'autenticacao:bloquear'"));
        Assert.Equal(3, await env.CountAsync(OrgA, "SELECT count(*) FROM auditoria_registros WHERE acao = 'autenticacao:falhar'"));
        var mensagens = await EntregarAsync(env);
        Assert.Equal(["dono@a.com", "gerente@a.com"], mensagens.Select(m => m.Para).Order().ToArray());
        Assert.All(mensagens, m => Assert.Equal("Conta bloqueada temporariamente", m.Assunto));
    }

    [Fact]
    public async Task Locking_an_unknown_name_leaves_no_event_no_audit_and_no_email()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres, tweak: o => o.Bloqueio = new LimitesDeLogin { FalhasParaBloquearEmail = 2, FalhasParaCaptchaConta = 100 });
        await env.CriarDonoAsync(OrgA, "dono@a.com");

        for (var i = 0; i < 4; i++)
        {
            await env.Auth.AutenticarPorEmailAsync("fantasma@a.com", "senha-errada-123456", new ContextoDeLogin(Casa, null));
        }

        Assert.Equal(0, await EventosAsync(env, ContaBloqueada.Type));
        Assert.Equal(0, await env.CountAsync(OrgA, "SELECT count(*) FROM auditoria_registros WHERE acao IN ('autenticacao:bloquear', 'autenticacao:falhar')"));
        Assert.Empty(await EntregarAsync(env));
    }

    [Fact]
    public async Task The_events_of_one_organization_are_delivered_with_the_recipients_of_that_organization_only()
    {
        await using var env = await IdentidadeEnv.CreateAsync(postgres);
        var donoA = await env.CriarDonoAsync(OrgA, "dono@a.com");
        await env.CriarDonoAsync(OrgB, "dono@b.com");
        await EntrarAsync(env, donoA, Casa, Chrome);
        await EntrarAsync(env, donoA, Bar, Firefox);

        var mensagens = await EntregarAsync(env);

        Assert.DoesNotContain(mensagens, m => m.Para == "dono@b.com");
    }
}
