using Flow4Tech.Kernel.Authorization;

namespace Flow4Tech.Organizacao.Application;

[PermissionCatalog]
public static class OrganizacaoPermissions
{
    public const string VerUnidades = "organizacao:ver-unidades";

    public const string TrocarUnidade = "organizacao:trocar-unidade";

    /// <summary>Read and change the layered settings of the organization and of the active unit (spec 5.7.3).</summary>
    public const string Configurar = "organizacao:configurar";
}
