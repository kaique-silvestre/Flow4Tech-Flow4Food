namespace Flow4Tech.Tests;

/// <summary>A tiny deterministic generator (SplitMix64) so a failing seed reproduces the same round; the property tests only need spread, not security.</summary>
internal sealed class Sorteio(int semente)
{
    private ulong _estado = (ulong)(uint)semente * 0x9E3779B97F4A7C15UL;

    public int Next() => (int)(Proximo() >> 33);

    public int Next(int limite) => Next() % limite;

    public int Next(int minimo, int limite) => minimo + (Next() % (limite - minimo));

    private ulong Proximo()
    {
        _estado += 0x9E3779B97F4A7C15UL;
        var z = _estado;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
