using Flow4Tech.Kernel;

namespace Flow4Tech.Tests;

public class KernelSmokeTests
{
    [Fact]
    public void EntityId_New_IsUuidV7()
    {
        Assert.Equal(7, EntityId.New().Value.Version);
    }

    [Fact]
    public void Money_HoldsIntegerCents()
    {
        var m = new Money(1050, "BRL");
        Assert.Equal(1050L, m.Cents);
    }
}
