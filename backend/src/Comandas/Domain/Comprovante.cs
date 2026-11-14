using System.Globalization;
using System.Text;

namespace Flow4Tech.Comandas.Domain;

public enum TipoDeComprovante
{
    /// <summary>The bill shown to the customer before paying.</summary>
    PreConta,

    /// <summary>The receipt of a closed comanda (the route always gives the second copy).</summary>
    Recibo,
}

/// <summary>One launched item as the customer reads it: frozen unit price (options included) and what the line charges (zero for a courtesy or a gift).</summary>
public sealed record LinhaDoComprovante(string Descricao, int Quantidade, long UnitarioCentavos, long TotalCentavos, string Tipo, string? Observacao, IReadOnlyList<string> Opcoes);

/// <summary>A discount given: <see cref="Alvo"/> is the item name, or "Comanda" for the whole bill; <see cref="AplicadoCentavos"/> is what the server computed when it was given.</summary>
public sealed record DescontoDoComprovante(string Alvo, string Forma, decimal? Percentual, long AplicadoCentavos, string Motivo);

/// <summary>A payment line without who took it: method name at the time, what was applied, the change, the tip, and the split part it paid.</summary>
public sealed record PagamentoDoComprovante(string MetodoNome, long AplicadoCentavos, long TrocoCentavos, long GorjetaCentavos, DateTimeOffset RegistradoEm, Guid? DivisaoId, int? DivisaoParte);

/// <summary>A part of a recorded split: what it owes of the bill, its share of the tip and what was already applied to it.</summary>
public sealed record ParteDoComprovante(int Numero, long ConsumoCentavos, long TaxaServicoCentavos, long TotalCentavos, long GorjetaCentavos, long PagoCentavos);

/// <summary>
/// The pre-bill or receipt of a comanda: data only, no fiscal document. It holds nothing but what the comanda holds (number, name, frozen items, discounts, fee, total, payments by method), never an
/// account id or a secret. <see cref="Partes"/> is set only when a split exists and still matches the bill.
/// </summary>
public sealed record Comprovante(
    TipoDeComprovante Tipo,
    bool SegundaVia,
    int Numero,
    string Identificacao,
    DateOnly DiaOperacional,
    DateTimeOffset AbertaEm,
    DateTimeOffset? FechadaEm,
    DateTimeOffset? ContaPedidaEm,
    DateTimeOffset GeradaEm,
    IReadOnlyList<LinhaDoComprovante> Itens,
    IReadOnlyList<DescontoDoComprovante> Descontos,
    ContaDaComanda Conta,
    IReadOnlyList<PagamentoDoComprovante> Pagamentos,
    IReadOnlyList<ParteDoComprovante>? Partes);

/// <summary>Pure rules of the pre-bill and the receipt (no I/O, no clock): the data and the plain text the phone shares. Order of the bill: ADR-018.</summary>
public static class RegrasDeComprovante
{
    private const int Largura = 40;

    /// <summary>Builds the document from the stored comanda. Cancelled items stay out; the bill is the ONE <see cref="RegrasDeComanda.Conta"/>; the parts come from the recorded split when given.</summary>
    public static Comprovante Montar(
        TipoDeComprovante tipo,
        Comanda comanda,
        IReadOnlyList<ItemDaComanda> itens,
        IReadOnlyList<Desconto> descontos,
        IReadOnlyList<PagamentoDoComprovante> pagamentos,
        IReadOnlyList<ParteDaDivisao>? partes,
        Guid? divisaoId,
        DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(comanda);
        ArgumentNullException.ThrowIfNull(itens);
        ArgumentNullException.ThrowIfNull(descontos);
        ArgumentNullException.ThrowIfNull(pagamentos);
        var vivos = itens.Where(i => i.Estado == EstadoDoItem.Lancado).ToList();
        var pago = pagamentos.Sum(p => p.AplicadoCentavos);
        var conta = RegrasDeComanda.Conta(RegrasDeRodada.Consumo(vivos), comanda.TaxaServicoPercentual, comanda.TaxaServicoRemovida, pago, RegrasDeDesconto.Calcular(vivos, descontos).TotalCentavos);
        var nomes = vivos.ToDictionary(i => i.Id, i => i.ProdutoNome);
        return new Comprovante(
            tipo,
            tipo == TipoDeComprovante.Recibo,
            comanda.Numero,
            comanda.Identificacao,
            comanda.DiaOperacional,
            comanda.AbertaEm,
            comanda.FechadaEm,
            comanda.ContaPedidaEm,
            agora,
            [.. vivos.Select(i => new LinhaDoComprovante(i.ProdutoNome, i.Quantidade, i.PrecoUnitarioCentavos, i.TotalCentavos, RegrasDeDesconto.Palavra(i.Tipo), i.Observacao, [.. i.Opcoes.Select(o => o.Nome)]))],
            [.. descontos.Select(d => new DescontoDoComprovante(d.ItemId is { } id && nomes.TryGetValue(id, out var nome) ? nome : "Comanda", RegrasDeDesconto.Palavra(d.Forma), d.Percentual, d.AplicadoCentavos, d.Motivo))],
            conta,
            pagamentos,
            partes is null
                ? null
                : [.. partes.Select(p => new ParteDoComprovante(p.Numero, p.ConsumoCentavos, p.TaxaServicoCentavos, p.TotalCentavos, p.GorjetaCentavos, pagamentos.Where(x => x.DivisaoId == divisaoId && x.DivisaoParte == p.Numero).Sum(x => x.AplicadoCentavos)))]);
    }

    /// <summary>Cents in reais, comma for decimals and dots for thousands (fixed, whatever the server culture).</summary>
    public static string Reais(long centavos)
    {
        var sinal = centavos < 0 ? "-" : string.Empty;
        var abs = Math.Abs(centavos);
        return string.Create(CultureInfo.InvariantCulture, $"{sinal}R$ {(abs / 100).ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.')},{abs % 100:D2}");
    }

    /// <summary>The plain document a phone can share: fixed 40 columns, LF line ends, always saying it has no fiscal value.</summary>
    public static string Texto(Comprovante c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var t = new StringBuilder();
        t.Append(c.Tipo == TipoDeComprovante.PreConta ? "PRÉ-CONTA" : "RECIBO - 2ª VIA").Append('\n');
        t.Append("Sem valor fiscal").Append('\n');
        t.Append(CultureInfo.InvariantCulture, $"Comanda {RegrasDeComanda.NumeroDeExibicao(c.Numero)} - {c.Identificacao}").Append('\n');
        t.Append(CultureInfo.InvariantCulture, $"Dia {c.DiaOperacional:dd/MM/yyyy}").Append('\n');
        t.Append(Linha('-')).Append('\n');
        foreach (var i in c.Itens)
        {
            t.Append(Par($"{i.Quantidade}x {i.Descricao}", Reais(i.TotalCentavos))).Append('\n');
            foreach (var o in i.Opcoes)
            {
                t.Append("   + ").Append(o).Append('\n');
            }

            if (i.Observacao is not null)
            {
                t.Append("   (").Append(i.Observacao).Append(")\n");
            }

            if (i.Tipo != "normal")
            {
                t.Append("   ").Append(i.Tipo).Append('\n');
            }
        }

        t.Append(Linha('-')).Append('\n');
        t.Append(Par("Consumo", Reais(c.Conta.ConsumoCentavos))).Append('\n');
        foreach (var d in c.Descontos)
        {
            t.Append(Par($"Desconto ({d.Alvo})", "-" + Reais(d.AplicadoCentavos))).Append('\n');
        }

        if (!c.Conta.TaxaServicoRemovida)
        {
            t.Append(Par(string.Create(CultureInfo.InvariantCulture, $"Taxa de serviço ({c.Conta.TaxaServicoPercentual:0.##}%)"), Reais(c.Conta.TaxaServicoCentavos))).Append('\n');
        }

        t.Append(Par("TOTAL", Reais(c.Conta.TotalCentavos))).Append('\n');
        if (c.Pagamentos.Count > 0)
        {
            t.Append(Linha('-')).Append('\n');
            foreach (var p in c.Pagamentos)
            {
                t.Append(Par(p.MetodoNome, Reais(p.AplicadoCentavos))).Append('\n');
                if (p.TrocoCentavos > 0)
                {
                    t.Append(Par("   Troco", Reais(p.TrocoCentavos))).Append('\n');
                }

                if (p.GorjetaCentavos > 0)
                {
                    t.Append(Par("   Gorjeta", Reais(p.GorjetaCentavos))).Append('\n');
                }
            }
        }

        if (c.Conta.SaldoCentavos != 0)
        {
            t.Append(Par("A pagar", Reais(c.Conta.SaldoCentavos))).Append('\n');
        }

        if (c.Partes is { Count: > 0 })
        {
            t.Append(Linha('-')).Append('\n').Append("Divisão da conta\n");
            foreach (var p in c.Partes)
            {
                t.Append(Par($"Parte {p.Numero}", Reais(p.TotalCentavos))).Append('\n');
            }
        }

        return t.ToString();
    }

    private static string Linha(char c) => new(c, Largura);

    private static string Par(string esquerda, string direita)
    {
        var espaco = Math.Max(1, Largura - esquerda.Length - direita.Length);
        return esquerda + new string(' ', espaco) + direita;
    }
}
