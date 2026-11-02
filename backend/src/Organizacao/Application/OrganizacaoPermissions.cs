using Flow4Tech.Kernel.Authorization;

namespace Flow4Tech.Organizacao.Application;

[PermissionCatalog]
public static class OrganizacaoPermissions
{
    public const string VerUnidades = "organizacao:ver-unidades";

    public const string TrocarUnidade = "organizacao:trocar-unidade";
}
