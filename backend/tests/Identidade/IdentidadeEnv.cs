using Flow4Tech.Identidade.Application;
using Flow4Tech.Identidade.Domain;
using Flow4Tech.Identidade.Infrastructure;
using Flow4Tech.Infrastructure.Audit;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Isolation.Tests;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static Flow4Tech.Isolation.Tests.TestDatabase;

namespace Flow4Tech.Identidade.Tests;

/// <summary>The host adapter of <see cref="IUnidadesDaConta"/> reads the Organizacao port; the tests read the same table directly.</summary>
internal sealed class SqlUnidadesDaConta : IUnidadesDaConta
{
    public async Task<IReadOnlyList<Guid>> ListarAsync(Guid organizacaoId, Guid contaId, CancellationToken cancellationToken)
    {
        var tx = TenantUnitOfWork.Require(organizacaoId);
        await using var command = new NpgsqlCommand("SELECT unidade_id FROM usuario_unidades WHERE usuario_id = @c ORDER BY unidade_id", tx.Connection, tx.Transaction);
        command.Parameters.AddWithValue("c", contaId);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
}

internal sealed record FakeRequest(Guid UserId, Guid SessionId, OrganizationContext Organization) : IRequestContext
{
    public FakeRequest(Guid userId, OrganizationContext organization)
        : this(userId, Guid.NewGuid(), organization)
    {
    }
}

/// <summary>Counts the hashing work, so a test can prove two login paths do the same work without timing anything.</summary>
internal sealed class CountingHasher(Argon2PasswordHasher inner) : IPasswordHasher, IDisposable
{
    public void Dispose() => inner.Dispose();

    public int Hashes { get; private set; }

    public int Verifications { get; private set; }

    public int DummyVerifications { get; private set; }

    public int Work => Hashes + Verifications + DummyVerifications;

    public void Reset()
    {
        Hashes = 0;
        Verifications = 0;
        DummyVerifications = 0;
    }

    public Task<string> HashAsync(string password, CancellationToken cancellationToken = default)
    {
        Hashes++;
        return inner.HashAsync(password, cancellationToken);
    }

    public Task<bool> VerifyAsync(string password, string encodedHash, CancellationToken cancellationToken = default)
    {
        Verifications++;
        return inner.VerifyAsync(password, encodedHash, cancellationToken);
    }

    public Task VerifyDummyAsync(string password, CancellationToken cancellationToken = default)
    {
        DummyVerifications++;
        return inner.VerifyDummyAsync(password, cancellationToken);
    }

    public bool NeedsRehash(string encodedHash) => inner.NeedsRehash(encodedHash);
}

internal sealed class CountingFloor : IResponseTimeFloor
{
    public int Calls { get; private set; }

    public Task AguardarAsync(long startedAtTimestamp, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.CompletedTask;
    }
}

internal sealed class FailingAudit(IAuditLog inner, string failOn) : IAuditLog
{
    public Task RecordAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default) =>
        entry.Action == failOn ? throw new InvalidOperationException("audit store unavailable") : inner.RecordAsync(organization, entry, cancellationToken);

    public Task RecordDetachedAsync(OrganizationContext organization, AuditEntry entry, CancellationToken cancellationToken = default) =>
        inner.RecordDetachedAsync(organization, entry, cancellationToken);
}

/// <summary>A migrated database with two tenants and the whole identity stack wired as the host will wire it (app credential, fast Argon2 parameters, no response-time floor).</summary>
internal sealed class IdentidadeEnv : IAsyncDisposable
{
    public const string Senha = "Senha-do-dono-123";

    public static readonly Argon2Options FastArgon = new() { MemoryKib = 1024, Iterations = 1, Parallelism = 1, MaxConcurrentHashes = 4 };

    private readonly ServiceProvider _services;

    private IdentidadeEnv(TestDatabase db, ServiceProvider services)
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

    public FixedClock Clock => _services.GetRequiredService<FixedClock>();

    public GestaoDeContas Contas => _services.GetRequiredService<GestaoDeContas>();

    public TrocaDeSenha Troca => _services.GetRequiredService<TrocaDeSenha>();

    public Autenticacao Auth => _services.GetRequiredService<Autenticacao>();

    public Sessoes Sessoes => _services.GetRequiredService<Sessoes>();

    public IPreAuthLookup Lookup => _services.GetRequiredService<IPreAuthLookup>();

    public IContaStore Store => _services.GetRequiredService<IContaStore>();

    public IUnitOfWork Uow => _services.GetRequiredService<IUnitOfWork>();

    public CountingHasher Hasher => (CountingHasher)_services.GetRequiredService<IPasswordHasher>();

    public CountingFloor Floor => (CountingFloor)_services.GetRequiredService<IResponseTimeFloor>();

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    public static async Task<IdentidadeEnv> CreateAsync(PostgresFixture postgres, Action<IdentidadeOptions>? tweak = null, Func<IAuditLog, IAuditLog>? decorateAudit = null, TestDatabase? existing = null, ICompromisedPasswordCheck? compromised = null)
    {
        var db = existing;
        if (db is null)
        {
            db = await postgres.CreateDatabaseAsync();
            await db.SeedTwoTenantsAsync();
        }

        var options = new IdentidadeOptions();
        options.Argon2 = FastArgon;
        options.TempoMinimoDeLogin = TimeSpan.Zero;
        options.Tokens.AppKey = "chave-de-teste-nao-usar-em-producao-0123456789";
        tweak?.Invoke(options);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new FixedClock());
        services.AddSingleton<IClock>(p => p.GetRequiredService<FixedClock>());
        services.AddTenantData(_ => db.AppConnectionString);
        services.AddAuditoria();
        services.AddIdentidade(o =>
        {
            o.Argon2 = options.Argon2;
            o.TempoMinimoDeLogin = options.TempoMinimoDeLogin;
            o.UsarHibp = false;
            o.CredencialTemporariaHoras = options.CredencialTemporariaHoras;
            o.HibpIndisponivelRecusa = options.HibpIndisponivelRecusa;
            o.Tokens = options.Tokens;
            o.Sessoes = options.Sessoes;
        });
        services.AddSingleton<IUnidadesDaConta, SqlUnidadesDaConta>();
        services.Replace(ServiceDescriptor.Singleton<IPasswordHasher>(_ => new CountingHasher(new Argon2PasswordHasher(options.Argon2))));
        services.Replace(ServiceDescriptor.Singleton<IResponseTimeFloor>(new CountingFloor()));
        if (compromised is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(compromised));
        }

        if (decorateAudit is not null)
        {
            var real = services.First(d => d.ServiceType == typeof(IAuditLog));
            services.Replace(ServiceDescriptor.Singleton<IAuditLog>(p => decorateAudit((IAuditLog)ActivatorUtilities.CreateInstance(p, real.ImplementationType!))));
        }

        return new IdentidadeEnv(db, services.BuildServiceProvider());
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await App.DisposeAsync();
        await Owner.DisposeAsync();
    }

    private async Task RunAsync(Guid org, Func<TenantTransaction, Task> work)
    {
        await using var tx = await Tenant.BeginAsync(Ctx(org));
        await work(tx);
        await tx.CommitAsync();
    }

    /// <summary>A second unit in the organization (the seed has one).</summary>
    public async Task<Guid> AddUnitAsync(Guid org)
    {
        var id = EntityId.New().Value;
        await RunAsync(org, tx => Exec(tx, "INSERT INTO unidades (id, organizacao_id, nome) VALUES (@i, @o, 'Outra')", ("i", id), ("o", org)));
        return id;
    }

    public Task VincularAsync(Guid org, Guid conta, Guid unidade) =>
        RunAsync(org, tx => Exec(tx, "INSERT INTO usuario_unidades (organizacao_id, usuario_id, unidade_id) VALUES (@o, @c, @u)", ("o", org), ("c", conta), ("u", unidade)));

    public Task DesvincularAsync(Guid org, Guid conta, Guid unidade) =>
        RunAsync(org, tx => Exec(tx, "DELETE FROM usuario_unidades WHERE organizacao_id = @o AND usuario_id = @c AND unidade_id = @u", ("o", org), ("c", conta), ("u", unidade)));

    /// <summary>A raw session row written with the app credential (to prove the composite foreign keys).</summary>
    public Task InsertSessionRowAsync(Guid org, Guid conta, Guid? unidade) =>
        RunAsync(org, tx => Exec(
            tx,
            """
            INSERT INTO sessoes (id, organizacao_id, conta_id, unidade_ativa_id, familia_id, versao_credencial, criada_em, ultima_atividade_em, expira_inatividade_em, expira_em)
            VALUES (@i, @o, @c, @u, @f, 1, now(), now(), now() + interval '1 hour', now() + interval '1 day')
            """,
            ("i", EntityId.New().Value), ("o", org), ("c", conta), ("u", (object?)unidade ?? DBNull.Value), ("f", EntityId.New().Value)));

    public static OrganizationContext Pre(Guid org) => OrganizationContext.WithoutUnit(org);

    /// <summary>The founding owner of an organization (their own password), by the same service the signup will use. Returns the account id.</summary>
    public async Task<Guid> CriarDonoAsync(Guid org, string email, string senha = Senha)
    {
        var result = await Contas.CriarDonoAsync(org, email, "Dono " + email, senha);
        Assert.Equal(StatusCriacao.Criada, result.Status);
        return result.ContaId!.Value;
    }

    /// <summary>A second owner: the founder only exists once per organization, so the second one is a manager named owner by the first (the real path).</summary>
    public async Task<Guid> CriarSegundoDonoAsync(Guid org, Guid primeiroDono, string email)
    {
        var id = await SeedGerenteAsync(org, email);
        Assert.True((await Contas.DefinirDonoAsync(As(primeiroDono, org), id, true)).Permitido);
        return id;
    }

    public static FakeRequest As(Guid user, Guid org) => new(user, Ctx(org));

    /// <summary>An operational account created by <paramref name="owner"/>; returns the id and the one-time credential.</summary>
    public async Task<(Guid Id, string Credencial)> CriarOperacionalAsync(Guid owner, Guid org, string usuario)
    {
        var result = await Contas.CriarOperacionalAsync(As(owner, org), usuario, "Operacional " + usuario);
        Assert.Equal(StatusCriacao.Criada, result.Status);
        return (result.ContaId!.Value, result.Credencial!.Valor);
    }

    /// <summary>A non-owner account with e-mail (manager). There is no invite flow yet (ticket 09), so it is stored through the same port the services use.</summary>
    public async Task<Guid> SeedGerenteAsync(Guid org, string email)
    {
        var id = EntityId.New().Value;
        var hash = await Hasher.HashAsync(Senha);
        await Uow.ExecuteAsync(Pre(org), async ct =>
        {
            Assert.True(await Store.InserirAsync(org, new NovaConta(id, TipoConta.Email, "Gerente " + email, email, null, false, Clock.UtcNow), hash, false, null, ct));
            Assert.True(await Store.RegistrarEmailAsync(org, id, email, ct));
        });
        return id;
    }

    public static async Task<List<T>> QueryAsync<T>(NpgsqlDataSource source, Guid org, string sql, Func<NpgsqlDataReader, T> map, params (string Name, object Value)[] args)
    {
        await using var tx = await new TenantDatabase(source).BeginAsync(Ctx(org));
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx.Transaction);
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    public async Task<long> CountAsync(Guid org, string sql, params (string Name, object Value)[] args) =>
        (await QueryAsync(App, org, sql, r => r.GetInt64(0), args))[0];

    public Task<long> AuditCountAsync(Guid org, string action, string? result = null) =>
        CountAsync(org, "SELECT count(*) FROM auditoria_registros WHERE acao = @a AND (@r::text IS NULL OR resultado = @r)", ("a", action), ("r", (object?)result ?? DBNull.Value));

    /// <summary>Does <paramref name="needle"/> appear in the text form of ANY row of ANY table, for either organization? (owner credential: the attacker with database access)</summary>
    public async Task<List<string>> TablesContainingAsync(string needle)
    {
        var found = new List<string>();
        var tables = new List<string>();
        await using (var connection = await Owner.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> 'schema_migrations'", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            foreach (var org in new[] { OrgA, OrgB })
            {
                var hits = await QueryAsync(Owner, org, $"SELECT count(*) FROM {table} t WHERE t::text LIKE '%' || @n || '%'", r => r.GetInt64(0), ("n", needle));
                if (hits[0] > 0)
                {
                    found.Add(table);
                    break;
                }
            }
        }

        return found;
    }
}
