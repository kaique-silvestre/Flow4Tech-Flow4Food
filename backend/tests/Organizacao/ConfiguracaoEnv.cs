using Flow4Tech.Infrastructure.Audit;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Settings;
using Flow4Tech.Organizacao.Application;
using Flow4Tech.Organizacao.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Organizacao.Tests;

internal sealed class RelogioFixo : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
}

internal sealed record PedidoDeTeste(Guid UserId, OrganizationContext Organization) : IRequestContext
{
    public Guid SessionId { get; } = Guid.NewGuid();
}

/// <summary>Who may configure: the test decides by account, as the host does through the one authorization engine.</summary>
internal sealed class PermissaoDeTeste : IPermissaoDoChamador
{
    public HashSet<Guid> Autorizados { get; } = [];

    public Task<bool> PodeAsync(IRequestContext request, string permission, CancellationToken cancellationToken) =>
        Task.FromResult(permission == OrganizacaoPermissions.Configurar && Autorizados.Contains(request.UserId));
}

/// <summary>A migrated database with two tenants and the settings stack wired as the host wires it (application credential, real audit chain).</summary>
internal sealed class ConfiguracaoEnv : IAsyncDisposable
{
    public static readonly Guid DonoA = Guid.Parse("0197a000-0000-7000-8000-0000000000d1");
    public static readonly Guid DonoB = Guid.Parse("0197b000-0000-7000-8000-0000000000d2");
    public static readonly Guid SemPermissao = Guid.Parse("0197a000-0000-7000-8000-0000000000d3");

    /// <summary>Layers: organization and unit, no explicit instant needed.</summary>
    public static readonly SettingDefinition Limite = new("teste.limite", SettingType.Inteiro, "10", SettingLayers.OrganizacaoEUnidade, "limite de teste") { Minimum = 1, Maximum = 100 };

    /// <summary>Like the operating day: needs an explicit start instant.</summary>
    public static readonly SettingDefinition Virada = new("teste.virada", SettingType.Hora, "05:00", SettingLayers.OrganizacaoEUnidade, "virada de teste") { RequiresEffectiveInstant = true };

    public static readonly SettingDefinition SoOrganizacao = new("teste.organizacao", SettingType.Texto, "a", SettingLayers.Organizacao, "so organizacao");

    public static readonly SettingDefinition SoSistema = new("teste.sistema", SettingType.Inteiro, "7", SettingLayers.SomenteSistema, "so sistema");

    private readonly ServiceProvider _services;

    private ConfiguracaoEnv(TestDatabase db, ServiceProvider services)
    {
        Db = db;
        _services = services;
        App = NpgsqlDataSource.Create(db.AppConnectionString);
        Owner = NpgsqlDataSource.Create(db.OwnerConnectionString);
        Tenant = new TenantDatabase(App);
        OwnerTenant = new TenantDatabase(Owner);
    }

    public TestDatabase Db { get; }

    public NpgsqlDataSource App { get; }

    public NpgsqlDataSource Owner { get; }

    public TenantDatabase Tenant { get; }

    public TenantDatabase OwnerTenant { get; }

    public RelogioFixo Clock => _services.GetRequiredService<RelogioFixo>();

    public PermissaoDeTeste Permissao => _services.GetRequiredService<PermissaoDeTeste>();

    public Configuracoes Configuracoes => _services.GetRequiredService<Configuracoes>();

    public ISettings Settings => _services.GetRequiredService<ISettings>();

    public IUnitOfWork UnitOfWork => _services.GetRequiredService<IUnitOfWork>();

    public static PedidoDeTeste ComoDonoA(Guid? unit = null) => new(DonoA, new OrganizationContext(OrgA, unit ?? Guid.Empty));

    public static async Task<ConfiguracaoEnv> CreateAsync(PostgresFixture postgres, Dictionary<string, string?>? sistema = null)
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        var services = new ServiceCollection();
        services.AddSingleton<RelogioFixo>();
        services.AddSingleton<IClock>(p => p.GetRequiredService<RelogioFixo>());
        services.AddSingleton<PermissaoDeTeste>();
        services.AddSingleton<IPermissaoDoChamador>(p => p.GetRequiredService<PermissaoDeTeste>());
        services.AddTenantData(_ => db.AppConnectionString);
        services.AddLogging();
        services.AddAuditoria();
        services.AddOrganizacao([Limite, Virada, SoOrganizacao, SoSistema, OrganizacaoSettings.FusoHorario, OrganizacaoSettings.ViradaDoDia], _ => d => sistema is not null && sistema.TryGetValue(d.SystemConfigPath, out var v) ? v : null);
        var env = new ConfiguracaoEnv(db, services.BuildServiceProvider());
        foreach (var (org, conta) in new[] { (OrgA, DonoA), (OrgA, SemPermissao), (OrgB, DonoB) })
        {
            await using var tx = await env.Tenant.BeginAsync(Ctx(org));
            await InsertContaAsync(tx, org, conta);
            await tx.CommitAsync();
        }

        env.Permissao.Autorizados.Add(DonoA);
        env.Permissao.Autorizados.Add(DonoB);
        return env;
    }

    public async Task<ResolvedSetting> ResolverAsync(SettingDefinition d, Guid org, Guid? unit, DateTimeOffset? em = null) =>
        await Settings.ResolveAsync(d, org, unit, em ?? Clock.UtcNow, CancellationToken.None);

    public async Task<List<string>> AuditoriaAsync(Guid org)
    {
        await using var tx = await Tenant.BeginAsync(Ctx(org));
        await using var command = new NpgsqlCommand("SELECT acao || '|' || resultado FROM auditoria_registros ORDER BY sequencia", tx.Connection, tx.Transaction);
        return await ReadAll(command);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await App.DisposeAsync();
        await Owner.DisposeAsync();
    }
}
