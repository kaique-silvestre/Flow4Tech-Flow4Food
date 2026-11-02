using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Identidade.Application;

/// <summary>Audit actions of the Identidade module (published names are never renamed, see backend/audit.catalog.txt).</summary>
[AuditCatalog]
public static class IdentidadeAuditActions
{
    public const string ContaCriar = "conta:criar";

    public const string ContaDesativar = "conta:desativar";

    public const string ContaDefinirDono = "conta:definir-dono";

    public const string ContaTrocarSenha = "conta:trocar-senha";

    public const string AutenticacaoFalhar = "autenticacao:falhar";
}
