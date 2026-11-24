using Flow4Tech.Clientes.Application;
using Flow4Tech.Clientes.Domain;
using Flow4Tech.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flow4Tech.Clientes.Infrastructure;

internal sealed class ClienteRow
{
    public Guid Id { get; set; }

    public Guid OrganizacaoId { get; set; }

    public string Nome { get; set; } = "";

    public string? Telefone { get; set; }

    public string? Email { get; set; }

    public string? Documento { get; set; }

    public bool ConsentimentoConcedido { get; set; }

    public DateTimeOffset? ConsentimentoEm { get; set; }

    public string? ConsentimentoFinalidade { get; set; }

    public string? ConsentimentoVersao { get; set; }

    public Guid? ConsentimentoPor { get; set; }

    public DateTimeOffset? ConsentimentoRevogadoEm { get; set; }

    public string Estado { get; set; } = "ativo";

    public DateTimeOffset CriadoEm { get; set; }

    public DateTimeOffset? AnonimizadoEm { get; set; }
}

/// <summary>EF mapping of the customers (schema owned by <c>0035_clientes.sql</c>). One instance per unit of work, from a tenant transaction.</summary>
internal sealed class ClientesDbContext(TenantTransaction transaction) : TenantDbContext(transaction)
{
    public DbSet<ClienteRow> Clientes => Set<ClienteRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ClienteRow>(e =>
        {
            e.ToTable("clientes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizacaoId).HasColumnName("organizacao_id");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.Telefone).HasColumnName("telefone");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.Documento).HasColumnName("documento");
            e.Property(x => x.ConsentimentoConcedido).HasColumnName("consentimento_concedido");
            e.Property(x => x.ConsentimentoEm).HasColumnName("consentimento_em");
            e.Property(x => x.ConsentimentoFinalidade).HasColumnName("consentimento_finalidade");
            e.Property(x => x.ConsentimentoVersao).HasColumnName("consentimento_versao");
            e.Property(x => x.ConsentimentoPor).HasColumnName("consentimento_por");
            e.Property(x => x.ConsentimentoRevogadoEm).HasColumnName("consentimento_revogado_em");
            e.Property(x => x.Estado).HasColumnName("estado");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em");
            e.Property(x => x.AnonimizadoEm).HasColumnName("anonimizado_em");
        });
    }
}

/// <summary>
/// The customers on PostgreSQL. Joins the caller's unit of work; RLS filters by organization (no query names it). Contacts are stored normalized, so the duplicate check is an equality.
/// No DELETE: the application credential cannot, a customer is anonymized instead.
/// </summary>
public sealed class PostgresClientesStore : IClientesStore, IConsultaDeVinculo
{
    private const string Ativo = "ativo";

    public async Task InserirAsync(Guid organizationId, Cliente cliente, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cliente);
        await using var context = new ClientesDbContext(TenantUnitOfWork.Require(organizationId));
        var c = cliente.Consentimento;
        _ = await context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO clientes (id, organizacao_id, nome, telefone, email, documento, consentimento_concedido, consentimento_em, consentimento_finalidade, consentimento_versao, consentimento_por, estado, criado_em)
            VALUES ({cliente.Id}, {organizationId}, {cliente.Nome}, {cliente.Telefone}, {cliente.Email}, {cliente.Documento}, {c.Concedido}, {c.Quando}, {c.Finalidade}, {c.VersaoDoTexto}, {c.RegistradoPor}, {Ativo}, {cliente.CriadoEm})
            """,
            cancellationToken);
    }

    public async Task<Cliente?> AcharAsync(Guid organizationId, Guid clienteId, CancellationToken cancellationToken)
    {
        await using var context = new ClientesDbContext(TenantUnitOfWork.Require(organizationId));
        var row = await context.Clientes.AsNoTracking().Where(c => c.Id == clienteId).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : Para(row);
    }

    public async Task<IReadOnlyList<Cliente>> BuscarAsync(Guid organizationId, string? texto, int limite, CancellationToken cancellationToken)
    {
        await using var context = new ClientesDbContext(TenantUnitOfWork.Require(organizationId));
        var consulta = context.Clientes.AsNoTracking().Where(c => c.Estado == Ativo);
        if (!string.IsNullOrEmpty(texto))
        {
            var nome = "%" + texto.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            var digitos = new string([.. texto.Where(char.IsAsciiDigit)]);
            var telefone = digitos.Length >= 3 ? "%" + digitos + "%" : null;
            consulta = consulta.Where(c => EF.Functions.ILike(c.Nome, nome) || (telefone != null && c.Telefone != null && EF.Functions.Like(c.Telefone, telefone)));
        }

        return [.. (await consulta.OrderBy(c => c.Nome).ThenBy(c => c.Id).Take(limite).ToListAsync(cancellationToken)).Select(Para)];
    }

    public async Task<IReadOnlyList<Cliente>> AcharDuplicadosAsync(Guid organizationId, string? telefone, string? email, string? documento, Guid? exceto, CancellationToken cancellationToken)
    {
        if (telefone is null && email is null && documento is null)
        {
            return [];
        }

        await using var context = new ClientesDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Clientes.AsNoTracking()
            .Where(c => c.Estado == Ativo && (exceto == null || c.Id != exceto) && ((telefone != null && c.Telefone == telefone) || (email != null && c.Email == email) || (documento != null && c.Documento == documento)))
            .OrderBy(c => c.Nome).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(Para)];
    }

    public async Task<bool> AtualizarAsync(Guid organizationId, Guid clienteId, string nome, string? telefone, string? email, string? documento, CancellationToken cancellationToken)
    {
        await using var context = new ClientesDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync($"UPDATE clientes SET nome = {nome}, telefone = {telefone}, email = {email}, documento = {documento} WHERE id = {clienteId} AND estado = {Ativo}", cancellationToken);
        return rows == 1;
    }

    public async Task<bool> RevogarConsentimentoAsync(Guid organizationId, Guid clienteId, DateTimeOffset quando, CancellationToken cancellationToken)
    {
        await using var context = new ClientesDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"UPDATE clientes SET consentimento_revogado_em = {quando} WHERE id = {clienteId} AND estado = {Ativo} AND consentimento_concedido AND consentimento_revogado_em IS NULL",
            cancellationToken);
        return rows == 1;
    }

    public async Task<bool> AnonimizarAsync(Guid organizationId, Guid clienteId, DateTimeOffset quando, CancellationToken cancellationToken)
    {
        await using var context = new ClientesDbContext(TenantUnitOfWork.Require(organizationId));
        var rows = await context.Database.ExecuteSqlAsync(
            $"""
            UPDATE clientes SET nome = {RegrasDeCliente.NomeAnonimizado}, telefone = NULL, email = NULL, documento = NULL, estado = 'anonimizado', anonimizado_em = {quando},
              consentimento_revogado_em = COALESCE(consentimento_revogado_em, {quando})
            WHERE id = {clienteId} AND estado = {Ativo}
            """,
            cancellationToken);
        return rows == 1;
    }

    public async Task<SituacaoParaVinculo> SituacaoAsync(Guid organizationId, Guid clienteId, CancellationToken cancellationToken)
    {
        var cliente = await AcharAsync(organizationId, clienteId, cancellationToken);
        return cliente switch
        {
            null => SituacaoParaVinculo.NaoEncontrado,
            { Estado: EstadoDoCliente.Anonimizado } => SituacaoParaVinculo.Anonimizado,
            { Consentimento.Vigente: false } => SituacaoParaVinculo.ConsentimentoRevogado,
            _ => SituacaoParaVinculo.Disponivel,
        };
    }

    private static Cliente Para(ClienteRow r) =>
        new(
            r.Id,
            r.Nome,
            r.Telefone,
            r.Email,
            r.Documento,
            new Consentimento(r.ConsentimentoConcedido, r.ConsentimentoEm, r.ConsentimentoFinalidade, r.ConsentimentoVersao, r.ConsentimentoPor, r.ConsentimentoRevogadoEm),
            r.Estado == Ativo ? EstadoDoCliente.Ativo : EstadoDoCliente.Anonimizado,
            r.CriadoEm,
            r.AnonimizadoEm);
}

public static class ClientesServices
{
    /// <summary>Registers the customers module. Needs <c>AddTenantData</c>, <c>AddAuditoria</c>, <c>IClock</c> and an <c>IPermissaoDeCliente</c> (adapted in the host).</summary>
    public static IServiceCollection AddClientes(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<PostgresClientesStore>();
        services.AddSingleton<IClientesStore>(p => p.GetRequiredService<PostgresClientesStore>());
        services.AddSingleton<IConsultaDeVinculo>(p => p.GetRequiredService<PostgresClientesStore>());
        services.AddSingleton<GestaoDeClientes>();
        return services;
    }
}
