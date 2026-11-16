using Flow4Tech.Pagamentos.Domain;
using FsCheck.Xunit;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0145: property tests of the refund rule. For EVERY paid amount and sequence of requests: the refunds accepted never add up to more than what the payment applied, the refundable balance never goes
/// below zero, a total refund empties it, and an amount outside 1..balance is never accepted. No I/O.
/// </summary>
public sealed class EstornoPropertyTests
{
    private static long Aplicado(uint bruto) => (bruto % 2_000_000) + 1;

    [Property]
    public bool The_sum_of_the_accepted_refunds_never_passes_what_was_paid_and_the_balance_never_goes_negative(uint bruto, long[] pedidos)
    {
        ArgumentNullException.ThrowIfNull(pedidos);
        var aplicado = Aplicado(bruto);
        var estornado = 0L;
        foreach (var pedido in pedidos)
        {
            var saldo = RegrasDeEstorno.SaldoEstornavel(aplicado, estornado);
            if (RegrasDeEstorno.Valor(pedido, saldo) is { } valor)
            {
                estornado += valor;
            }

            if (estornado > aplicado || RegrasDeEstorno.SaldoEstornavel(aplicado, estornado) < 0)
            {
                return false;
            }
        }

        return true;
    }

    [Property]
    public bool An_amount_below_one_cent_or_above_the_balance_is_never_accepted(uint bruto, uint ja, long pedido)
    {
        var aplicado = Aplicado(bruto);
        var saldo = RegrasDeEstorno.SaldoEstornavel(aplicado, ja % (aplicado + 1));
        return RegrasDeEstorno.Valor(pedido, saldo) is { } valor ? valor >= 1 && valor <= saldo && valor == pedido : pedido < 1 || pedido > saldo;
    }

    [Property]
    public bool A_total_refund_takes_all_that_is_left_and_empties_the_balance(uint bruto, uint ja)
    {
        var aplicado = Aplicado(bruto);
        var estornado = ja % aplicado;
        var saldo = RegrasDeEstorno.SaldoEstornavel(aplicado, estornado);
        var total = RegrasDeEstorno.Valor(null, saldo);
        return total == saldo && RegrasDeEstorno.SaldoEstornavel(aplicado, estornado + total!.Value) == 0 && RegrasDeEstorno.Valor(null, 0) is null;
    }

    [Property]
    public bool The_reason_is_required_trimmed_and_limited(string? motivo)
    {
        var aceito = RegrasDeEstorno.TentarMotivo(motivo, out var texto);
        return aceito == (texto.Length is >= 1 and <= RegrasDeEstorno.MotivoMaximo && !texto.Any(char.IsControl)) && texto == (motivo?.Trim() ?? "");
    }
}
