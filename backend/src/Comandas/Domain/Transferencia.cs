namespace Flow4Tech.Comandas.Domain;

/// <summary>Why a move of items (transfer, separation or merge) is refused by the rules; <see cref="Nenhuma"/> means it is accepted.</summary>
public enum RecusaDeMovimento
{
    Nenhuma,

    /// <summary>The selection is empty, repeats an id, has an empty id or is above the limit.</summary>
    SelecaoInvalida,

    ItemNaoEncontrado,

    ItemJaCancelado,

    /// <summary>The item carries a discount of its own: that row is written once and tied to the item, so it cannot follow it.</summary>
    ItemComDesconto,

    /// <summary>A comanda that is merged away carries no discount: a discount row cannot move to the other comanda.</summary>
    ComDesconto,

    /// <summary>What the origin received would exceed what is left of its bill.</summary>
    ValorJaRecebido,
}

/// <summary>Pure rules of moving items between two comandas of a unit (no I/O). The handler decides who may, in which state and with which versions; these decide only what can move.</summary>
public static class RegrasDeTransferencia
{
    public const int ItensMaximo = 200;

    public static bool SelecaoValida(IReadOnlyCollection<Guid>? ids) =>
        ids is { Count: >= 1 and <= ItensMaximo } && ids.All(i => i != Guid.Empty) && ids.Distinct().Count() == ids.Count;

    /// <summary>The selected items can leave the origin: all launched, none with a discount of its own, and the bill that stays still covers what the origin already received.</summary>
    public static RecusaDeMovimento Itens(IReadOnlyCollection<Guid> selecionados, IReadOnlyList<ItemDaComanda> itensDaOrigem, IReadOnlyList<Desconto> descontosDaOrigem, decimal taxaPercentual, bool taxaRemovida, long pagoCentavos)
    {
        ArgumentNullException.ThrowIfNull(selecionados);
        ArgumentNullException.ThrowIfNull(itensDaOrigem);
        ArgumentNullException.ThrowIfNull(descontosDaOrigem);
        if (!SelecaoValida(selecionados))
        {
            return RecusaDeMovimento.SelecaoInvalida;
        }

        var escolhidos = new List<ItemDaComanda>();
        foreach (var id in selecionados)
        {
            var item = itensDaOrigem.FirstOrDefault(i => i.Id == id);
            if (item is null)
            {
                return RecusaDeMovimento.ItemNaoEncontrado;
            }

            if (item.Estado == EstadoDoItem.Cancelado)
            {
                return RecusaDeMovimento.ItemJaCancelado;
            }

            if (descontosDaOrigem.Any(d => d.ItemId == id))
            {
                return RecusaDeMovimento.ItemComDesconto;
            }

            escolhidos.Add(item);
        }

        var ficam = itensDaOrigem.Where(i => !selecionados.Contains(i.Id)).ToList();
        var conta = RegrasDeComanda.Conta(RegrasDeRodada.Consumo(ficam), taxaPercentual, taxaRemovida, pagoCentavos, RegrasDeDesconto.Calcular(ficam, descontosDaOrigem).TotalCentavos);
        return conta.SaldoCentavos < 0 ? RecusaDeMovimento.ValorJaRecebido : RecusaDeMovimento.Nenhuma;
    }

    /// <summary>The comanda can be merged into another: it received nothing (its bill goes to zero) and holds no discount.</summary>
    public static RecusaDeMovimento Juncao(IReadOnlyList<ItemDaComanda> itensDaOrigem, IReadOnlyList<Desconto> descontosDaOrigem, long pagoCentavos)
    {
        ArgumentNullException.ThrowIfNull(itensDaOrigem);
        ArgumentNullException.ThrowIfNull(descontosDaOrigem);
        if (pagoCentavos > 0)
        {
            return RecusaDeMovimento.ValorJaRecebido;
        }

        return descontosDaOrigem.Count > 0 ? RecusaDeMovimento.ComDesconto : RecusaDeMovimento.Nenhuma;
    }

    /// <summary>The items a merge carries: the launched ones (cancelled items stay behind with their history).</summary>
    public static IReadOnlyList<Guid> IdsParaJuntar(IEnumerable<ItemDaComanda> itensDaOrigem)
    {
        ArgumentNullException.ThrowIfNull(itensDaOrigem);
        return itensDaOrigem.Where(i => i.Estado == EstadoDoItem.Lancado).Select(i => i.Id).ToList();
    }
}
