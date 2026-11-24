using System.Globalization;

namespace Flow4Tech.Comandas.Domain;

public enum TipoDePromocao
{
    /// <summary>A percent off each unit of the listed products.</summary>
    Percentual,

    /// <summary>A fixed amount off each unit of the listed products.</summary>
    Valor,

    /// <summary>The listed products, in the listed quantities, at one fixed price per full set.</summary>
    Combo,
}

/// <summary>A product of a rule and how many units a full set needs (always 1 outside a combo).</summary>
public sealed record ProdutoDaPromocao(Guid ProdutoId, int Quantidade);

/// <summary>
/// A promotion or combo rule. <see cref="UnidadeId"/> null means every unit. Days of the week are a bitmask (bit 0 = Sunday .. bit 6 = Saturday, <see cref="DayOfWeek"/>); the optional time window is the unit's
/// LOCAL time and, when <see cref="HoraFim"/> is not after <see cref="HoraInicio"/>, crosses midnight (the days then name the day the window STARTS). <see cref="ValorCentavos"/> is the amount off per unit
/// (<see cref="TipoDePromocao.Valor"/>) or the price of one full set (<see cref="TipoDePromocao.Combo"/>).
/// </summary>
public sealed record Promocao(
    Guid Id,
    Guid? UnidadeId,
    string Nome,
    TipoDePromocao Tipo,
    decimal? Percentual,
    long? ValorCentavos,
    bool Ativo,
    DateTimeOffset? ValidoDe,
    DateTimeOffset? ValidoAte,
    int DiasDaSemana,
    TimeOnly? HoraInicio,
    TimeOnly? HoraFim,
    int Versao,
    IReadOnlyList<ProdutoDaPromocao> Produtos,
    Guid CriadoPor,
    DateTimeOffset CriadoEm,
    Guid AtualizadoPor,
    DateTimeOffset AtualizadoEm);

/// <summary>One line of a round, as the rule sees it: the product price alone, the unit price with its options, and the quantity.</summary>
public sealed record LinhaParaPromocao(int Indice, Guid ProdutoId, long PrecoCentavos, long UnitarioCentavos, int Quantidade)
{
    public long TotalCentavos => UnitarioCentavos * Quantidade;
}

/// <summary>What a rule took off one line of the round. Never more than the line charges.</summary>
public sealed record PromocaoAplicada(int Indice, Guid PromocaoId, string Nome, long DescontoCentavos);

/// <summary>Pure rules of promotions and combos (no I/O). The result feeds the ordinary item discount of ADR-018 (step 2, before any manual discount).</summary>
public static class RegrasDePromocao
{
    public const int TodosOsDias = 127;

    public const int NomeMaximo = 80;

    public const int ProdutosMaximo = 20;

    public const int QuantidadeMaxima = 99;

    public static string Palavra(TipoDePromocao tipo) => tipo switch
    {
        TipoDePromocao.Percentual => "percentual",
        TipoDePromocao.Valor => "valor",
        TipoDePromocao.Combo => "combo",
        _ => throw new ArgumentOutOfRangeException(nameof(tipo)),
    };

    public static bool TentarTipo(string? palavra, out TipoDePromocao tipo)
    {
        (var ok, tipo) = palavra switch
        {
            "percentual" => (true, TipoDePromocao.Percentual),
            "valor" => (true, TipoDePromocao.Valor),
            "combo" => (true, TipoDePromocao.Combo),
            _ => (false, default),
        };
        return ok;
    }

    /// <summary>The trimmed name, or null when it is empty, too long or has control characters.</summary>
    public static string? NomeValido(string? nome)
    {
        var limpo = nome?.Trim();
        return string.IsNullOrEmpty(limpo) || limpo.Length > NomeMaximo || limpo.Any(char.IsControl) ? null : limpo;
    }

    /// <summary>Days (0 = Sunday .. 6 = Saturday) to the bitmask; null when empty, repeated or out of range. A null list means every day.</summary>
    public static int? MascaraDeDias(IReadOnlyList<int>? dias)
    {
        if (dias is null)
        {
            return TodosOsDias;
        }

        var mascara = 0;
        foreach (var dia in dias)
        {
            if (dia is < 0 or > 6 || (mascara & (1 << dia)) != 0)
            {
                return null;
            }

            mascara |= 1 << dia;
        }

        return mascara == 0 ? null : mascara;
    }

    public static IReadOnlyList<int> DiasDaMascara(int mascara) => [.. Enumerable.Range(0, 7).Where(d => (mascara & (1 << d)) != 0)];

    /// <summary>"HH:mm" to a time of day; false for anything else.</summary>
    public static bool TentarHora(string? texto, out TimeOnly hora) =>
        TimeOnly.TryParseExact(texto, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out hora);

    public static string Texto(TimeOnly hora) => hora.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static bool AplicaNaUnidade(Promocao promocao, Guid unidadeId)
    {
        ArgumentNullException.ThrowIfNull(promocao);
        return promocao.UnidadeId is null || promocao.UnidadeId == unidadeId;
    }

    /// <summary>
    /// Is the rule in force at the instant? <paramref name="utc"/> judges the absolute validity (from inclusive, until exclusive); <paramref name="local"/> is the same instant on the unit's wall clock and judges the
    /// days and the time window. A window that crosses midnight belongs to the day it started: Friday 22:00 to 02:00 covers Saturday 01:00 when Friday is allowed.
    /// </summary>
    public static bool VigenteEm(Promocao promocao, DateTimeOffset utc, DateTime local)
    {
        ArgumentNullException.ThrowIfNull(promocao);
        if (!promocao.Ativo || (promocao.ValidoDe is { } de && utc < de) || (promocao.ValidoAte is { } ate && utc >= ate))
        {
            return false;
        }

        if (promocao.HoraInicio is not { } inicio || promocao.HoraFim is not { } fim)
        {
            return DiaPermitido(promocao.DiasDaSemana, local.DayOfWeek);
        }

        var agora = TimeOnly.FromDateTime(local);
        if (inicio < fim)
        {
            return DiaPermitido(promocao.DiasDaSemana, local.DayOfWeek) && agora >= inicio && agora < fim;
        }

        if (agora >= inicio)
        {
            return DiaPermitido(promocao.DiasDaSemana, local.DayOfWeek);
        }

        return agora < fim && DiaPermitido(promocao.DiasDaSemana, (DayOfWeek)(((int)local.DayOfWeek + 6) % 7));
    }

    private static bool DiaPermitido(int mascara, DayOfWeek dia) => (mascara & (1 << (int)dia)) != 0;

    /// <summary>
    /// What the rules in force take off the lines of one round. Each line takes AT MOST one promotion. Combos go first, the biggest saving per set first (ties by id): a combo needs every one of its products
    /// in the round, forms as many full sets as the units allow, and its saving (table value of the sets minus the set price, never below zero) is shared among the lines it used in proportion to their product
    /// price (the last one takes the rounding remainder); a line a combo used takes no other promotion. Every other line takes the best of the percent and fixed-amount rules that list its product (ties by
    /// id), with the percent rounded by the one rounding of ADR-018. Options are not part of the product price a combo or a fixed amount refers to, but a percent is taken from the whole line. Nothing is ever
    /// taken beyond what its line charges, so the total cannot go below zero.
    /// </summary>
    public static IReadOnlyList<PromocaoAplicada> Avaliar(IReadOnlyList<LinhaParaPromocao> linhas, IReadOnlyList<Promocao> vigentes)
    {
        ArgumentNullException.ThrowIfNull(linhas);
        ArgumentNullException.ThrowIfNull(vigentes);
        var aplicadas = new List<PromocaoAplicada>();
        var usadas = new HashSet<int>();

        foreach (var combo in vigentes.Where(p => p.Tipo == TipoDePromocao.Combo && p.Produtos.Count > 0 && p.ValorCentavos is not null).OrderByDescending(p => EconomiaPorConjunto(p, linhas)).ThenBy(p => p.Id))
        {
            AplicarCombo(combo, linhas, usadas, aplicadas);
        }

        foreach (var linha in linhas.Where(l => !usadas.Contains(l.Indice) && l.TotalCentavos > 0))
        {
            Promocao? melhor = null;
            long melhorDesconto = 0;
            foreach (var p in vigentes.Where(p => p.Tipo != TipoDePromocao.Combo && p.Produtos.Any(x => x.ProdutoId == linha.ProdutoId)).OrderBy(p => p.Id))
            {
                var desconto = p.Tipo == TipoDePromocao.Percentual
                    ? RegrasDeDesconto.DoPercentual(linha.TotalCentavos, p.Percentual ?? 0m)
                    : Math.Min(linha.TotalCentavos, (p.ValorCentavos ?? 0) * linha.Quantidade);
                desconto = Math.Min(desconto, linha.TotalCentavos);
                if (desconto > melhorDesconto)
                {
                    melhor = p;
                    melhorDesconto = desconto;
                }
            }

            if (melhor is not null)
            {
                aplicadas.Add(new PromocaoAplicada(linha.Indice, melhor.Id, melhor.Nome, melhorDesconto));
            }
        }

        return aplicadas;
    }

    private static long PrecoDoProduto(IReadOnlyList<LinhaParaPromocao> linhas, Guid produtoId) =>
        linhas.Where(l => l.ProdutoId == produtoId).Select(l => l.PrecoCentavos).DefaultIfEmpty(0).Min();

    /// <summary>Saving of one full set (table price of its units minus the set price), zero when the round lacks a product or the combo does not save.</summary>
    private static long EconomiaPorConjunto(Promocao combo, IReadOnlyList<LinhaParaPromocao> linhas)
    {
        if (combo.Produtos.Any(p => linhas.All(l => l.ProdutoId != p.ProdutoId)))
        {
            return 0;
        }

        var tabela = combo.Produtos.Sum(p => p.Quantidade * PrecoDoProduto(linhas, p.ProdutoId));
        return Math.Max(0, tabela - (combo.ValorCentavos ?? 0));
    }

    private static void AplicarCombo(Promocao combo, IReadOnlyList<LinhaParaPromocao> linhas, HashSet<int> usadas, List<PromocaoAplicada> aplicadas)
    {
        var livres = linhas.Where(l => !usadas.Contains(l.Indice) && l.TotalCentavos > 0).ToList();
        var disponivel = livres.GroupBy(l => l.ProdutoId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantidade));
        var conjuntos = combo.Produtos.Min(p => disponivel.GetValueOrDefault(p.ProdutoId) / p.Quantidade);
        var economia = EconomiaPorConjunto(combo, livres);
        if (conjuntos < 1 || economia < 1)
        {
            return;
        }

        var desconto = economia * conjuntos;
        var tabela = combo.Produtos.Sum(p => (long)p.Quantidade * conjuntos * PrecoDoProduto(livres, p.ProdutoId));
        var partes = new List<(LinhaParaPromocao Linha, long Valor)>();
        foreach (var p in combo.Produtos)
        {
            var preco = PrecoDoProduto(livres, p.ProdutoId);
            var falta = (long)p.Quantidade * conjuntos;
            foreach (var linha in livres.Where(l => l.ProdutoId == p.ProdutoId))
            {
                if (falta == 0)
                {
                    break;
                }

                var unidades = Math.Min(falta, linha.Quantidade);
                falta -= unidades;
                partes.Add((linha, (long)((Int128)desconto * unidades * preco / tabela)));
            }
        }

        var resto = desconto - partes.Sum(p => p.Valor);
        for (var i = 0; i < partes.Count; i++)
        {
            var (linha, valor) = partes[i];
            var parte = Math.Min(linha.TotalCentavos, i == partes.Count - 1 ? valor + resto : valor);
            usadas.Add(linha.Indice);
            if (parte >= 1)
            {
                aplicadas.Add(new PromocaoAplicada(linha.Indice, combo.Id, combo.Nome, parte));
            }
        }
    }
}
