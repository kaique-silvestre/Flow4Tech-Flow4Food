using Flow4Tech.Permissoes.Domain;

namespace Flow4Tech.Permissoes.Application;

/// <summary>
/// A ready-made profile an owner can create in one tap: a key (stable, for the client), the display name and the permissions it carries. <paramref name="Limites"/> are the default limits of
/// some of those permissions (a permission of <paramref name="Permissoes"/> without one is unlimited); the owner edits them later like any rule.
/// </summary>
public sealed record ModeloDePerfil(string Chave, string Nome, IReadOnlyList<string> Permissoes, IReadOnlyList<RegraDeAcesso> Limites);

/// <summary>
/// The model profiles of the salão (waiter, cashier, manager). They are DATA: a list of names and permissions that <see cref="GestaoDePerfis.CriarModelosAsync"/> turns into
/// ordinary custom profiles; nothing reads a profile name afterwards. Permissions of later tickets of the phase are added to these lists as they land (a permission the catalog
/// does not know yet is skipped on creation, so adding it here early is harmless).
/// </summary>
public static class ModelosDePerfil
{
    private static readonly string[] Garcom =
    [
        "comanda:abrir", "comanda:ver", "comanda:lancar-item", "comanda:cancelar-item", "comanda:desfazer-rodada", "comanda:transferir-mesa", "comanda:alterar-item", "comanda:renomear", "comanda:alterar-dados", "comanda:transferir-item", "comanda:dividir", "mesa:ver", "produto:ver",
    ];

    private static readonly string[] Caixa =
    [
        "comanda:ver", "comanda:ver-comandas-de-outros", "comanda:receber-pagamento", "comanda:fechar", "comanda:remover-taxa-servico", "comanda:dividir", "comanda:reimprimir",
        "pagamento:registrar", "pagamento:ver", "pagamento:dividir", "pagamento:registrar-gorjeta", "caixa:ver", "caixa:abrir", "caixa:fechar", "caixa:suprimento", "caixa:sangria", "caixa:fechar-cego", "produto:ver", "mesa:ver",
    ];

    // The manager does everything of the salão except managing profiles and permissions (an owner-only decision until the owner says otherwise).
    private static readonly string[] Gerente =
    [
        "comanda:abrir", "comanda:ver", "comanda:ver-comandas-de-outros", "comanda:lancar-item", "comanda:cancelar-item", "comanda:desfazer-rodada",
        "comanda:receber-pagamento", "comanda:fechar", "comanda:remover-taxa-servico", "comanda:transferir-mesa", "comanda:alterar-item", "comanda:renomear", "comanda:alterar-dados",
        "comanda:trocar-garcom", "comanda:cancelar", "comanda:ver-historico", "comanda:transferir-item", "comanda:juntar", "comanda:dividir", "pagamento:registrar", "pagamento:ver",
        "pagamento:dividir", "pagamento:registrar-gorjeta", "metodo-pagamento:gerenciar", "caixa:ver", "caixa:ver-todos", "caixa:abrir", "caixa:fechar", "caixa:suprimento", "caixa:sangria", "caixa:fechar-cego", "caixa:aprovar-diferenca", "mesa:ver", "mesa:gerenciar", "produto:ver", "produto:gerenciar",
        "comanda:desconto", "comanda:cortesia", "comanda:brinde", "comanda:reimprimir",
    ];

    // Default limits (numbers a restaurant is expected to edit; the owner is the factory profile, uncapped; no daily cap by default).
    // Cashier (F4-0065): moves up to R$ 500,00 in and R$ 1.000,00 out per movement and closes blind when the unit asks for it. It does not approve a difference: the model that closes is not the one
    // that approves (separation of duties).
    private static readonly RegraDeAcesso[] LimitesDoCaixa =
    [
        new(RuleEffect.Allow, "caixa:suprimento", MaxValueCents: 50_000),
        new(RuleEffect.Allow, "caixa:sangria", MaxValueCents: 100_000),
    ];

    // Manager. Discount (F4-0118): up to 20% and R$ 150,00 per discount; courtesy and gift are always measured at 100%, so they carry only a value cap, R$ 80,00 of table value. Cash register
    // (F4-0065): moves up to R$ 5.000,00 per movement and approves a total difference up to R$ 200,00; above that only the owner decides.
    private static readonly RegraDeAcesso[] LimitesDoGerente =
    [
        new(RuleEffect.Allow, "comanda:desconto", MaxPercent: 20m, MaxValueCents: 15_000),
        new(RuleEffect.Allow, "comanda:cortesia", MaxValueCents: 8_000),
        new(RuleEffect.Allow, "comanda:brinde", MaxValueCents: 8_000),
        new(RuleEffect.Allow, "caixa:suprimento", MaxValueCents: 500_000),
        new(RuleEffect.Allow, "caixa:sangria", MaxValueCents: 500_000),
        new(RuleEffect.Allow, "caixa:aprovar-diferenca", MaxValueCents: 20_000),
    ];

    public static IReadOnlyList<ModeloDePerfil> Todos { get; } =
    [
        new("garcom", "Garçom", Garcom, []),
        new("caixa", "Caixa", Caixa, LimitesDoCaixa),
        new("gerente", "Gerente", Gerente, LimitesDoGerente),
    ];

    /// <summary>The rules a model becomes: its limited rule where it has one, a plain allow otherwise. Permissions the catalog does not know are the caller's to skip.</summary>
    public static IEnumerable<RegraDeAcesso> Regras(ModeloDePerfil modelo)
    {
        ArgumentNullException.ThrowIfNull(modelo);
        return modelo.Permissoes.Select(p => modelo.Limites.FirstOrDefault(l => l.Permission == p) ?? new RegraDeAcesso(RuleEffect.Allow, p));
    }
}
