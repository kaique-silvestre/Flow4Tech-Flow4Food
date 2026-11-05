using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Identidade.Infrastructure;

namespace Flow4Tech.Identidade.Tests;

/// <summary>Spec 5.5.1 and 5.5.2: progressive lock per account AND per address, different limits per kind of account, credential stuffing, adaptive CAPTCHA. No database.</summary>
public sealed class LimitadorDeLoginTests
{
    private static readonly ChaveDeLogin Maria = new(TipoConta.Operacional, "org:maria");
    private static readonly ChaveDeLogin Dono = new(TipoConta.Email, "dono@a.com");

    private readonly FixedClock _clock = new();

    private LimitadorDeLoginEmMemoria Limitador(LimitesDeLogin? limites = null) => new(_clock, limites ?? new LimitesDeLogin());

    private static void Falhar(LimitadorDeLoginEmMemoria limitador, ChaveDeLogin chave, string? ip, int vezes)
    {
        for (var i = 0; i < vezes; i++)
        {
            limitador.RegistrarFalha(chave, ip);
        }
    }

    [Fact]
    public void Attempts_are_free_until_the_limit_of_the_kind_of_account_is_reached()
    {
        var limitador = Limitador();

        Falhar(limitador, Dono, null, 2);

        Assert.IsType<Livre>(limitador.Avaliar(Dono, null));
    }

    [Fact]
    public void An_account_with_email_locks_at_its_limit_and_an_operational_one_at_a_different_limit()
    {
        var limites = new LimitesDeLogin { FalhasParaBloquearEmail = 5, FalhasParaBloquearOperacional = 8 };
        var limitador = Limitador(limites);

        Falhar(limitador, Dono, null, 4);
        Falhar(limitador, Maria, null, 4);
        Assert.IsNotType<Bloqueada>(limitador.Avaliar(Dono, null));
        limitador.RegistrarFalha(Dono, null);
        Assert.IsType<Bloqueada>(limitador.Avaliar(Dono, null));

        Falhar(limitador, Maria, null, 3); // 7 failures: the operational limit is 8
        Assert.IsNotType<Bloqueada>(limitador.Avaliar(Maria, null));
        limitador.RegistrarFalha(Maria, null);
        Assert.IsType<Bloqueada>(limitador.Avaliar(Maria, null));
    }

    [Fact]
    public void The_lock_grows_with_every_failure_after_the_limit_up_to_a_ceiling()
    {
        var limites = new LimitesDeLogin { FalhasParaBloquearEmail = 3, BloqueioBase = TimeSpan.FromSeconds(30), BloqueioMaximo = TimeSpan.FromMinutes(2) };
        var limitador = Limitador(limites);
        var duracoes = new List<TimeSpan>();

        Falhar(limitador, Dono, null, 2);
        for (var i = 0; i < 5; i++)
        {
            var efeito = limitador.RegistrarFalha(Dono, null);
            duracoes.Add(efeito.Conta!.Ate - _clock.UtcNow);
            _clock.UtcNow += efeito.Conta.Ate - _clock.UtcNow + TimeSpan.FromSeconds(1); // wait for the lock to end, then fail again
        }

        Assert.Equal([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(120)], duracoes);
    }

    [Fact]
    public void A_lock_ends_by_itself_and_the_blocked_answer_says_until_when()
    {
        var limitador = Limitador(new LimitesDeLogin { FalhasParaBloquearEmail = 2, BloqueioBase = TimeSpan.FromSeconds(30) });
        Falhar(limitador, Dono, null, 2);

        var bloqueada = Assert.IsType<Bloqueada>(limitador.Avaliar(Dono, null));
        Assert.Equal(_clock.UtcNow + TimeSpan.FromSeconds(30), bloqueada.Ate);
        Assert.Equal(MotivoDeBloqueio.TentativasDaConta, bloqueada.Motivo);

        _clock.UtcNow += TimeSpan.FromSeconds(31);
        Assert.IsNotType<Bloqueada>(limitador.Avaliar(Dono, null));
    }

    [Fact]
    public void A_start_of_a_lock_is_reported_once_not_on_every_failure_inside_it()
    {
        var limitador = Limitador(new LimitesDeLogin { FalhasParaBloquearEmail = 2 });

        Assert.Null(limitador.RegistrarFalha(Dono, null).Conta);
        Assert.NotNull(limitador.RegistrarFalha(Dono, null).Conta);
    }

    [Fact]
    public void Failures_far_apart_do_not_add_up()
    {
        var limites = new LimitesDeLogin { FalhasParaBloquearEmail = 3, JanelaDeFalhas = TimeSpan.FromMinutes(30) };
        var limitador = Limitador(limites);
        Falhar(limitador, Dono, null, 2);

        _clock.UtcNow += TimeSpan.FromMinutes(31);
        limitador.RegistrarFalha(Dono, null);

        Assert.IsNotType<Bloqueada>(limitador.Avaliar(Dono, null));
    }

    [Fact]
    public void A_successful_login_clears_the_failures_of_the_account()
    {
        var limitador = Limitador(new LimitesDeLogin { FalhasParaBloquearEmail = 3 });
        Falhar(limitador, Dono, null, 2);

        limitador.RegistrarSucesso(Dono);
        Falhar(limitador, Dono, null, 2);

        Assert.IsNotType<Bloqueada>(limitador.Avaliar(Dono, null));
    }

    [Fact]
    public void An_address_locks_after_too_many_failures_whoever_the_accounts_are()
    {
        var limites = new LimitesDeLogin { FalhasParaBloquearIp = 6, ContasDistintasPorIp = 100, FalhasParaBloquearEmail = 100 };
        var limitador = Limitador(limites);

        for (var i = 0; i < 5; i++)
        {
            limitador.RegistrarFalha(new ChaveDeLogin(TipoConta.Email, $"u{i % 2}@a.com"), "203.0.113.9");
        }

        Assert.IsNotType<Bloqueada>(limitador.Avaliar(Dono, "203.0.113.9"));
        var efeito = limitador.RegistrarFalha(Dono, "203.0.113.9");

        Assert.Equal(MotivoDeBloqueio.TentativasDoIp, efeito.Ip!.Motivo);
        Assert.IsType<Bloqueada>(limitador.Avaliar(Maria, "203.0.113.9")); // any account from that address
        Assert.IsNotType<Bloqueada>(limitador.Avaliar(Maria, "203.0.113.10")); // another address is not affected
    }

    [Fact]
    public void Credential_stuffing_many_accounts_from_one_address_locks_that_address()
    {
        var limites = new LimitesDeLogin { ContasDistintasPorIp = 4, BloqueioDeStuffing = TimeSpan.FromMinutes(15) };
        var limitador = Limitador(limites);
        BloqueioIniciado? ip = null;

        for (var i = 0; i < 4; i++)
        {
            ip = limitador.RegistrarFalha(new ChaveDeLogin(TipoConta.Email, $"vitima{i}@a.com"), "198.51.100.7").Ip;
        }

        Assert.Equal(MotivoDeBloqueio.ContasDemaisPorIp, ip!.Motivo);
        Assert.Equal(_clock.UtcNow + TimeSpan.FromMinutes(15), ip.Ate);
        Assert.IsType<Bloqueada>(limitador.Avaliar(Dono, "198.51.100.7"));
    }

    [Fact]
    public void The_same_account_failing_many_times_from_one_address_is_not_stuffing()
    {
        var limitador = Limitador(new LimitesDeLogin { ContasDistintasPorIp = 2, FalhasParaBloquearEmail = 100, FalhasParaBloquearIp = 100 });

        Falhar(limitador, Dono, "198.51.100.7", 20);

        Assert.IsNotType<Bloqueada>(limitador.Avaliar(Dono, "198.51.100.7"));
    }

    [Fact]
    public void Credential_stuffing_one_account_from_many_addresses_locks_the_account()
    {
        var limites = new LimitesDeLogin { IpsDistintosPorConta = 3, FalhasParaBloquearEmail = 100, BloqueioDeStuffing = TimeSpan.FromMinutes(15) };
        var limitador = Limitador(limites);
        BloqueioIniciado? conta = null;

        for (var i = 0; i < 3; i++)
        {
            conta = limitador.RegistrarFalha(Dono, $"198.51.100.{i}").Conta;
        }

        Assert.Equal(MotivoDeBloqueio.IpsDemaisPorConta, conta!.Motivo);
        var bloqueada = Assert.IsType<Bloqueada>(limitador.Avaliar(Dono, "203.0.113.1")); // even from a brand-new address
        Assert.Equal(MotivoDeBloqueio.IpsDemaisPorConta, bloqueada.Motivo);
    }

    [Fact]
    public void An_unknown_address_is_not_tracked_by_address()
    {
        var limitador = Limitador(new LimitesDeLogin { FalhasParaBloquearIp = 1, ContasDistintasPorIp = 1 });

        for (var i = 0; i < 20; i++)
        {
            limitador.RegistrarFalha(new ChaveDeLogin(TipoConta.Email, $"u{i}@a.com"), null);
        }

        Assert.IsNotType<Bloqueada>(limitador.Avaliar(Dono, null));
    }

    [Fact]
    public void A_captcha_is_asked_before_the_lock_when_failures_pile_up()
    {
        var limites = new LimitesDeLogin { FalhasParaCaptchaConta = 2, FalhasParaBloquearEmail = 5 };
        var limitador = Limitador(limites);

        Falhar(limitador, Dono, null, 1);
        Assert.IsType<Livre>(limitador.Avaliar(Dono, null));
        Falhar(limitador, Dono, null, 1);

        Assert.IsType<ExigeCaptcha>(limitador.Avaliar(Dono, null));
        Assert.IsType<Livre>(limitador.Avaliar(Maria, null)); // another account is not affected
    }

    [Fact]
    public void The_memory_is_bounded_and_a_full_table_still_works_for_the_keys_it_holds()
    {
        var limitador = Limitador(new LimitesDeLogin { MaximoDeRegistros = 50, FalhasParaBloquearEmail = 2 });

        for (var i = 0; i < 500; i++)
        {
            limitador.RegistrarFalha(new ChaveDeLogin(TipoConta.Email, $"lixo{i}@a.com"), null);
        }

        Assert.True(limitador.Registros <= 50);
        Falhar(limitador, new ChaveDeLogin(TipoConta.Email, "lixo0@a.com"), null, 5); // a held key keeps being counted
        Assert.IsType<Bloqueada>(limitador.Avaliar(new ChaveDeLogin(TipoConta.Email, "lixo0@a.com"), null));
    }

    [Fact]
    public void Stale_entries_are_dropped_to_make_room()
    {
        var limitador = Limitador(new LimitesDeLogin { MaximoDeRegistros = 10, JanelaDeFalhas = TimeSpan.FromMinutes(30) });
        for (var i = 0; i < 10; i++)
        {
            limitador.RegistrarFalha(new ChaveDeLogin(TipoConta.Email, $"velho{i}@a.com"), null);
        }

        _clock.UtcNow += TimeSpan.FromMinutes(45);
        limitador.RegistrarFalha(new ChaveDeLogin(TipoConta.Email, "novo@a.com"), null);

        Assert.True(limitador.Registros <= 10);
        Falhar(limitador, new ChaveDeLogin(TipoConta.Email, "novo@a.com"), null, 5);
        Assert.IsType<Bloqueada>(limitador.Avaliar(new ChaveDeLogin(TipoConta.Email, "novo@a.com"), null));
    }
}
