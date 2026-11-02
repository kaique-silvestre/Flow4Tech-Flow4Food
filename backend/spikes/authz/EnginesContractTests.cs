using Flow4Tech.Authorization.Tests;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;

namespace Authz.Spike;

/// <summary>The exact contract the production engine passes (tests/Authorization), run against Cedar and Casbin.</summary>
public sealed class CedarEngineContractTests : AuthorizationEngineContract
{
    protected override IAuthorizationEngine Create(PolicySet policies, ICumulativeLimitStore limits, Flow4Tech.Kernel.IClock clock) => new CedarAuthorizationEngine(policies, limits, clock);
}

public sealed class CasbinEngineContractTests : AuthorizationEngineContract
{
    protected override IAuthorizationEngine Create(PolicySet policies, ICumulativeLimitStore limits, Flow4Tech.Kernel.IClock clock) => new CasbinAuthorizationEngine(policies, limits, clock);
}
