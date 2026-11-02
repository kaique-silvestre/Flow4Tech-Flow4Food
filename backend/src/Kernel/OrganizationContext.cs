namespace Flow4Tech.Kernel;

/// <summary>Identifies the tenant (organization) and unit a request runs in. Placeholder.</summary>
public sealed record OrganizationContext(Guid OrganizationId, Guid UnitId);
