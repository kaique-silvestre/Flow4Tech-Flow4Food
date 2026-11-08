namespace Flow4Tech.Permissoes.Application;

/// <summary>A ready-made profile an owner can create in one tap: a key (stable, for the client), the display name and the permissions it carries.</summary>
public sealed record ModeloDePerfil(string Chave, string Nome, IReadOnlyList<string> Permissoes);

/// <summary>
/// The model profiles of the salão (waiter, cashier, manager). They are DATA: a list of names and permissions that <see cref="GestaoDePerfis.CriarModelosAsync"/> turns into
/// ordinary custom profiles; nothing reads a profile name afterwards. Permissions of later tickets of the phase are added to these lists as they land (a permission the catalog
/// does not know yet is skipped on creation, so adding it here early is harmless).
/// </summary>
public static class ModelosDePerfil
{
    private static readonly string[] Garcom =
    [
        "comanda:abrir", "comanda:ver", "comanda:lancar-item", "comanda:cancelar-item", "comanda:desfazer-rodada", "mesa:ver", "produto:ver",
    ];

    private static readonly string[] Caixa =
    [
        "comanda:ver", "comanda:ver-comandas-de-outros", "comanda:receber-pagamento", "comanda:fechar", "comanda:remover-taxa-servico",
        "pagamento:registrar", "pagamento:ver", "caixa:ver", "caixa:abrir", "caixa:fechar", "produto:ver", "mesa:ver",
    ];

    // The manager does everything of the salão except managing profiles and permissions (an owner-only decision until the owner says otherwise).
    private static readonly string[] Gerente =
    [
        "comanda:abrir", "comanda:ver", "comanda:ver-comandas-de-outros", "comanda:lancar-item", "comanda:cancelar-item", "comanda:desfazer-rodada",
        "comanda:receber-pagamento", "comanda:fechar", "comanda:remover-taxa-servico", "pagamento:registrar", "pagamento:ver",
        "caixa:ver", "caixa:ver-todos", "caixa:abrir", "caixa:fechar", "mesa:ver", "mesa:gerenciar", "produto:ver", "produto:gerenciar",
    ];

    public static IReadOnlyList<ModeloDePerfil> Todos { get; } =
    [
        new("garcom", "Garçom", Garcom),
        new("caixa", "Caixa", Caixa),
        new("gerente", "Gerente", Gerente),
    ];
}
