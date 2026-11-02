namespace Flow4Tech.Kernel;

/// <summary>Entity identifier. Always a UUID v7 (time-ordered).</summary>
public readonly record struct EntityId(Guid Value)
{
    public static EntityId New() => new(Guid.CreateVersion7());
}
