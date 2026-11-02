namespace DataAccess.Spike;

public sealed record Item(Guid Id, Guid OrganizacaoId, Guid? UnidadeId, string Nome, long ValorCentavos);

public interface IItemStore
{
    Task AddAsync(Flow4Tech.Kernel.OrganizationContext ctx, Item item);

    Task<IReadOnlyList<Item>> ListAsync(Flow4Tech.Kernel.OrganizationContext ctx);

    /// <summary>Inserts, then throws InvalidOperationException before commit (the unit of work must roll back).</summary>
    Task AddThenFailAsync(Flow4Tech.Kernel.OrganizationContext ctx, Item item);
}

public static class Stores
{
    public static IItemStore Create(string impl, Flow4Tech.Infrastructure.Data.TenantDatabase tenant) => impl switch
    {
        "raw" => new RawStore(tenant),
        "dapper" => new DapperStore(tenant),
        "ef" => new EfStore(tenant),
        _ => throw new ArgumentOutOfRangeException(nameof(impl)),
    };
}
