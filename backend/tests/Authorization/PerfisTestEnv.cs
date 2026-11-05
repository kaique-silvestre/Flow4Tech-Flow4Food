using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Flow4Tech.Permissoes.Infrastructure;
using Npgsql;
using static Flow4Tech.Authorization.Tests.Fixtures;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Authorization.Tests;

/// <summary>An authenticated request as the Application layer sees it.</summary>
internal sealed record TestRequest(Guid UserId, OrganizationContext Organization) : IRequestContext
{
    public Guid SessionId { get; } = Guid.NewGuid();
}

/// <summary>Audit log that remembers what it was given (the real one is covered by the Audit tests).</summary>
internal sealed class RecordingAuditLog : IAuditLog
{
    public List<AuditEntry> Attached { get; } = [];

    public List<AuditEntry> Detached { get; } = [];

    public Task RecordAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        // Same rule as the real log: joining needs a unit of work in progress.
        TenantUnitOfWork.Require(organization.OrganizationId);
        Attached.Add(entry);
        return Task.CompletedTask;
    }

    public Task RecordDetachedAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default)
    {
        if (TenantUnitOfWork.InProgress(organization.OrganizationId))
        {
            throw new InvalidOperationException("A detached record is written after the unit of work ended.");
        }

        Detached.Add(entry);
        return Task.CompletedTask;
    }
}

/// <summary>
/// What the host's profile source does, without the Identidade module: the factory key of each account (a dictionary), the custom profiles assigned to it (the store) and its
/// private override key. An account missing from the dictionary holds nothing.
/// </summary>
internal sealed class TestPerfis(IUnitOfWork unitOfWork, IPerfilStore store, Dictionary<Guid, string> factory) : IPerfisDoUsuario
{
    public Task<IReadOnlyList<string>> ListarAsync(OrganizationContext organization, Guid userId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            organization,
            async ct =>
            {
                if (!factory.TryGetValue(userId, out var key))
                {
                    return (IReadOnlyList<string>)[];
                }

                var keys = new List<string> { key, ChavesDePerfil.DoUsuario(userId) };
                keys.AddRange((await store.PerfisDoUsuarioAsync(organization.OrganizationId, userId, ct)).Select(ChavesDePerfil.Personalizado));
                return keys;
            },
            cancellationToken);
}

/// <summary>Everything the persisted-profiles tests need on one real database: tenant A with unit 1 and unit 2, the application credential, the real store and policy source.</summary>
internal sealed class PerfisTestEnv : IAsyncDisposable
{
    public static readonly string[] FullCatalog =
    [
        .. Catalog,
        PerfisPermissions.Ver,
        PerfisPermissions.Gerir,
        PerfisPermissions.Atribuir,
        "organizacao:ver-unidades",
        "organizacao:trocar-unidade",
        "sessao:trocar-senha",
        "sessao:ver-proprias",
        "sessao:revogar-propria",
        "conta:gerir-google",
    ];

    private readonly NpgsqlDataSource _source;

    private PerfisTestEnv(NpgsqlDataSource source)
    {
        _source = source;
        Tenant = new TenantDatabase(source);
        Uow = new TenantUnitOfWork(Tenant);
        Store = new PostgresPerfilStore();
        Factory = PoliticaDeFabrica.Criar(FullCatalog);
        Policies = new PostgresPolicySource(Tenant, Factory);
        Clock = new FixedClock(Now);
        Limits = new PostgresCumulativeLimitStore();
        Engine = new RulesAuthorizationEngine(Policies, Limits, new ConfiguredOperatingDayResolver(Clock));
        Audit = new RecordingAuditLog();
        Perfis = new TestPerfis(Uow, Store, FactoryKeys);
        Gestao = new GestaoDePerfis(Uow, Engine, Policies, Perfis, Store, Audit, Clock);
        Simulador = new SimuladorDePermissoes(Uow, Engine, Perfis);
    }

    public TenantDatabase Tenant { get; }

    public TenantUnitOfWork Uow { get; }

    public PostgresPerfilStore Store { get; }

    public PolicySet Factory { get; }

    public PostgresPolicySource Policies { get; }

    public FixedClock Clock { get; }

    public PostgresCumulativeLimitStore Limits { get; }

    public RulesAuthorizationEngine Engine { get; }

    public RecordingAuditLog Audit { get; }

    public TestPerfis Perfis { get; }

    public GestaoDePerfis Gestao { get; }

    public SimuladorDePermissoes Simulador { get; }

    /// <summary>The factory key of each account the test created (owner or plain account).</summary>
    public Dictionary<Guid, string> FactoryKeys { get; } = [];

    public static OrganizationContext OrgContext => new(OrgA, UnitA);

    public static async Task<PerfisTestEnv> CreateAsync(PostgresFixture postgres)
    {
        var db = await postgres.CreateDatabaseAsync();
        await db.SeedTwoTenantsAsync();
        await AddUnit2Async(db);
        return new PerfisTestEnv(NpgsqlDataSource.Create(db.AppConnectionString + ";Maximum Pool Size=100"));
    }

    /// <summary>Unit 2 of tenant A (the seed only has unit 1).</summary>
    public static async Task AddUnit2Async(TestDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        await using var owner = NpgsqlDataSource.Create(db.OwnerConnectionString);
        var tenant = new TenantDatabase(owner);
        await using var tx = await tenant.BeginAsync(Ctx(OrgA));
        await Exec(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@u, @o, 'A-Norte')", ("u", Unit2), ("o", OrgA));
        await tx.CommitAsync();
    }

    /// <summary>Creates an account in tenant A and registers its factory key; <paramref name="owner"/> true gives the factory owner profile.</summary>
    public async Task<Guid> AddAccountAsync(bool owner)
    {
        var id = Guid.NewGuid();
        await Uow.ExecuteAsync(OrgContext, async ct =>
        {
            var tx = TenantUnitOfWork.Require(OrgA);
            await InsertContaAsync(tx, OrgA, id);
            await Task.CompletedTask;
        });
        FactoryKeys[id] = owner ? PerfisDeFabrica.Dono : PerfisDeFabrica.Conta;
        return id;
    }

    public static TestRequest As(Guid userId) => new(userId, OrgContext);

    /// <summary>The keys the host would give the engine for this person right now.</summary>
    public Task<IReadOnlyList<string>> KeysOfAsync(Guid userId) => Perfis.ListarAsync(OrgContext, userId, CancellationToken.None);

    public async Task<Decision> DecideAsync(Guid userId, string permission, Guid? unit = null, DecisionContext? context = null)
    {
        var keys = await KeysOfAsync(userId);
        return await Engine.DecidirAsync(new Subject(userId, keys), permission, new Scope(OrgA, unit), context ?? new DecisionContext());
    }

    public async Task<int> CredentialVersionAsync(Guid userId)
    {
        await using var tx = await Tenant.BeginAsync(OrgContext);
        await using var command = new NpgsqlCommand("SELECT versao_credencial FROM contas WHERE id = @i", tx.Connection, tx.Transaction);
        command.Parameters.AddWithValue("i", userId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    public async Task<long> ConsumedAsync(Guid userId, string permission)
    {
        await using var tx = await Tenant.BeginAsync(OrgContext);
        await using var command = new NpgsqlCommand("SELECT COALESCE(SUM(consumido_centavos), 0)::bigint FROM limites_consumo WHERE usuario_id = @u AND permissao = @p", tx.Connection, tx.Transaction);
        command.Parameters.AddWithValue("u", userId);
        command.Parameters.AddWithValue("p", permission);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public ValueTask DisposeAsync()
    {
        return _source.DisposeAsync();
    }
}
