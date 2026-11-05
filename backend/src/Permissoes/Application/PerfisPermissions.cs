using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;

namespace Flow4Tech.Permissoes.Application;

/// <summary>Permissions of the management of profiles, rules and assignments (spec 5.4.3 to 5.4.6).</summary>
[PermissionCatalog]
public static class PerfisPermissions
{
    /// <summary>See profiles, their rules and the effective rights of a person (the simulator).</summary>
    public const string Ver = "perfil:ver";

    /// <summary>Create, duplicate and edit custom profiles.</summary>
    public const string Gerir = "perfil:gerir";

    /// <summary>Give or take a profile or an individual override from a person.</summary>
    public const string Atribuir = "permissao:atribuir";
}

/// <summary>Audit actions of the management of profiles (spec 5.6.2: permission and profile changes).</summary>
[AuditCatalog]
public static class PerfisAuditActions
{
    public const string PerfilCriar = "perfil:criar";
    public const string PerfilEditarRegras = "perfil:editar-regras";
    public const string PerfilAtribuir = "perfil:atribuir";
    public const string PerfilRemoverAtribuicao = "perfil:remover-atribuicao";
    public const string PermissaoDefinirOverrides = "permissao:definir-overrides";

    /// <summary>An attempt to change one's own rights or to hand on what one does not hold. Always result <c>negado</c>.</summary>
    public const string PermissaoNegarEscalada = "permissao:negar-escalada";
}
