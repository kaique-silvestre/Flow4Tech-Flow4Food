using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;

namespace Flow4Tech.Authorization.Tests;

/// <summary>Shared vocabulary of the authorization tests: the profiles, permissions, units and the policy every engine is given.</summary>
public static class Fixtures
{
    public const string Garcom = "garcom";
    public const string Gerente = "gerente";
    public const string Caixa = "caixa";
    public const string Bloqueado = "bloqueado";

    public const string Desconto = "desconto:conceder";
    public const string Cortesia = "cortesia:conceder";
    public const string FecharCaixa = "caixa:fechar";
    public const string AjustarEstoque = "estoque:ajustar";
    public const string Relatorio = "relatorio:ver";

    public static readonly Guid OrgId = Guid.Parse("0197a000-0000-7000-8000-00000000000a");
    public static readonly Guid Unit1 = Guid.Parse("0197a000-0000-7000-8000-0000000000a1");
    public static readonly Guid Unit2 = Guid.Parse("0197a000-0000-7000-8000-0000000000a2");
    public static readonly Guid User = Guid.Parse("0197a000-0000-7000-8000-0000000000c1");
    public static readonly DateTimeOffset Now = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    public static readonly string[] Catalog = [Desconto, Cortesia, FecharCaixa, AjustarEstoque, Relatorio];

    public static IReadOnlyList<Rule> StandardRules { get; } =
    [
        // Case 1: the waiter gives discounts up to 10% by profile; the manager up to 30%.
        new(RuleEffect.Allow, Garcom, Desconto, MaxPercent: 10m),
        new(RuleEffect.Allow, Gerente, Desconto, MaxPercent: 30m),

        // Case 2: courtesy with a cumulative daily cap of R$ 50,00 for the waiter (cents), none for the manager.
        new(RuleEffect.Allow, Garcom, Cortesia, DailyCapCents: 5_000),
        new(RuleEffect.Allow, Gerente, Cortesia),

        // Case 3: the manager adjusts stock only on unit 2.
        new(RuleEffect.Allow, Gerente, AjustarEstoque, UnitId: Unit2),

        new(RuleEffect.Allow, Caixa, FecharCaixa),
        new(RuleEffect.Allow, Gerente, Relatorio),

        // Explicit deny: a blocked user can do nothing, whatever other profile they hold.
        new(RuleEffect.Deny, Bloqueado, Desconto),
        new(RuleEffect.Deny, Bloqueado, Cortesia),
        new(RuleEffect.Deny, Bloqueado, Relatorio),
    ];

    public static PolicySet Standard() => new(Catalog, StandardRules);

    public static Subject Who(params string[] profiles) => new(User, profiles);

    public static Scope At(Guid? unit = null) => new(OrgId, unit);

    public static DecisionContext Ctx(decimal? percent = null, long? cents = null, string currency = "BRL") =>
        new(cents is null ? null : new Money(cents.Value, currency), percent);
}

/// <summary>Settable server clock for the tests.</summary>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>Thread-safe in-memory counter with the same contract as the PostgreSQL one; lets the unit suite run without a database.</summary>
public sealed class InMemoryCumulativeLimitStore : ICumulativeLimitStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(Guid, Guid, string, DateOnly), long> _totals = [];

    public int Consumptions { get; private set; }

    public long Total(Guid organizationId, Guid userId, string permission, DateOnly day)
    {
        lock (_gate)
        {
            return _totals.GetValueOrDefault((organizationId, userId, permission, day));
        }
    }

    public Task<bool> TryConsumeAsync(Guid organizationId, Guid? unitId, Guid userId, string permission, DateOnly day, long cents, long capCents, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = (organizationId, userId, permission, day);
            var next = _totals.GetValueOrDefault(key) + cents;
            if (cents < 0 || next > capCents)
            {
                return Task.FromResult(false);
            }

            _totals[key] = next;
            Consumptions++;
            return Task.FromResult(true);
        }
    }
}
