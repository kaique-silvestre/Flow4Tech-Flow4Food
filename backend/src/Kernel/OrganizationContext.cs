namespace Flow4Tech.Kernel;

/// <summary>Identifies the tenant (organization) and unit a request runs in. Placeholder.</summary>
public sealed record OrganizationContext(Guid OrganizationId, Guid UnitId)
{
    /// <summary>
    /// A context for work that happens BEFORE a session has an active unit (login, the first password change): only the organization,
    /// which must come from the database (the account that was just found), never from the client. Application code of the Identidade module is
    /// the only caller allowed (<c>PreSessionContextTests</c>); everything else gets its context from <see cref="IRequestContext"/>.
    /// </summary>
    public static OrganizationContext WithoutUnit(Guid organizationId) => new(organizationId, Guid.Empty);
}
