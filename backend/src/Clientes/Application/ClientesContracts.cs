using Flow4Tech.Clientes.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;

namespace Flow4Tech.Clientes.Application;

[PermissionCatalog]
public static class ClientesPermissions
{
    /// <summary>List and search customers and open one. Without <see cref="VerDadosPessoais"/> phone, e-mail and document come masked; the name never does.</summary>
    public const string Ver = "cliente:ver";

    /// <summary>Register a customer (the LGPD consent is required).</summary>
    public const string Cadastrar = "cliente:cadastrar";

    /// <summary>Change a customer's data and revoke the consent.</summary>
    public const string Editar = "cliente:editar";

    /// <summary>See phone, e-mail and document unmasked. Asked by the use case to decide WHAT is returned; the route needs only <see cref="Ver"/>.</summary>
    public const string VerDadosPessoais = "cliente:ver-dados-pessoais";

    /// <summary>Anonymize ("forget") a customer: erases the personal data, keeps the id and the financial history.</summary>
    public const string Anonimizar = "cliente:anonimizar";

    /// <summary>Link a customer to a comanda or unlink it.</summary>
    public const string Vincular = "cliente:vincular";
}

[AuditCatalog]
public static class ClientesAuditActions
{
    public const string Cadastrar = "cliente:cadastrar";

    public const string Editar = "cliente:editar";

    public const string RevogarConsentimento = "cliente:revogar-consentimento";

    public const string Anonimizar = "cliente:anonimizar";
}

/// <summary>Port: the customer rows of the current organization. Runs inside a unit of work (RLS filters; nothing here names the organization in a filter).</summary>
public interface IClientesStore
{
    Task InserirAsync(Guid organizationId, Cliente cliente, CancellationToken cancellationToken);

    Task<Cliente?> AcharAsync(Guid organizationId, Guid clienteId, CancellationToken cancellationToken);

    /// <summary>Active customers whose name contains the text (any case) or whose phone contains its digits (3 or more); no text lists them all. By name, at most <paramref name="limite"/>.</summary>
    Task<IReadOnlyList<Cliente>> BuscarAsync(Guid organizationId, string? texto, int limite, CancellationToken cancellationToken);

    /// <summary>Active customers (other than <paramref name="exceto"/>) with the same phone, the same e-mail or the same document; a null contact is not compared.</summary>
    Task<IReadOnlyList<Cliente>> AcharDuplicadosAsync(Guid organizationId, string? telefone, string? email, string? documento, Guid? exceto, CancellationToken cancellationToken);

    /// <summary>Replaces name and contacts of an ACTIVE customer; false when it is not active.</summary>
    Task<bool> AtualizarAsync(Guid organizationId, Guid clienteId, string nome, string? telefone, string? email, string? documento, CancellationToken cancellationToken);

    /// <summary>Marks the consent of an ACTIVE customer as revoked at the instant; false when it was not granted, already revoked, or the customer is not active.</summary>
    Task<bool> RevogarConsentimentoAsync(Guid organizationId, Guid clienteId, DateTimeOffset quando, CancellationToken cancellationToken);

    /// <summary>Erases name and contacts and marks the customer anonymized (revoking a consent still in force); false when it was already anonymized.</summary>
    Task<bool> AnonimizarAsync(Guid organizationId, Guid clienteId, DateTimeOffset quando, CancellationToken cancellationToken);
}

public enum SituacaoParaVinculo
{
    /// <summary>No customer of the organization with this id.</summary>
    NaoEncontrado,

    Disponivel,

    Anonimizado,

    /// <summary>The consent was revoked: the customer is not linked to anything new.</summary>
    ConsentimentoRevogado,
}

/// <summary>Port (public API of the module): lets another module ask whether a customer of the current organization can be linked, without reading the customer's data.</summary>
public interface IConsultaDeVinculo
{
    Task<SituacaoParaVinculo> SituacaoAsync(Guid organizationId, Guid clienteId, CancellationToken cancellationToken);
}

/// <summary>Port: asks the one authorization engine for the caller (adapted in Api). Decides what is returned (personal data masked or not), never whether the route runs.</summary>
public interface IPermissaoDeCliente
{
    Task<bool> PodeAsync(IRequestContext request, string permission, CancellationToken cancellationToken);
}

/// <summary>What a customer looks like to the caller. When <see cref="DadosMascarados"/> is true phone, e-mail and document are masked (<c>***-**12</c>); the name, the consent and the state never are.</summary>
public sealed record ClienteExibido(Guid Id, string Nome, string? Telefone, string? Email, string? Documento, bool DadosMascarados, Consentimento Consentimento, EstadoDoCliente Estado, DateTimeOffset CriadoEm)
{
    public static ClienteExibido De(Cliente c, bool verDados)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new ClienteExibido(
            c.Id,
            c.Nome,
            Campo(c.Telefone, verDados, RegrasDeCliente.MascararDigitos),
            Campo(c.Email, verDados, RegrasDeCliente.MascararEmail),
            Campo(c.Documento, verDados, RegrasDeCliente.MascararDigitos),
            !verDados,
            c.Consentimento,
            c.Estado,
            c.CriadoEm);
    }

    private static string? Campo(string? valor, bool verDados, Func<string, string> mascara) => valor is null || verDados ? valor : mascara(valor);
}

/// <summary>The fields of a customer as the caller sent them (not yet normalized). Blank contacts count as absent.</summary>
public sealed record DadosDoCliente(string? Nome, string? Telefone, string? Email, string? Documento);

/// <summary>The consent as the caller sent it: <see cref="Concedido"/> must be true to register; the purpose is a code and the text version identifies what the customer read.</summary>
public sealed record ConsentimentoInformado(bool Concedido, string? Finalidade, string? VersaoDoTexto);

public enum ResultadoDoCliente
{
    Ok,

    /// <summary>A field is empty, too long or malformed.</summary>
    Invalido,

    /// <summary>The registration came without an explicit, granted consent.</summary>
    ConsentimentoObrigatorio,

    /// <summary>Another customer already has this phone, e-mail or document and the caller did not confirm. <see cref="ResultadoDeCliente.Candidatos"/> lists them.</summary>
    PossivelDuplicidade,

    NaoEncontrado,

    Anonimizado,

    /// <summary>The consent was revoked: the data is not changed any more.</summary>
    ConsentimentoRevogado,

    /// <summary>Revoking a consent that was already revoked (or never granted).</summary>
    ConsentimentoJaRevogado,
}

public sealed record ResultadoDeCliente(ResultadoDoCliente Resultado, ClienteExibido? Cliente = null, IReadOnlyList<ClienteExibido>? Candidatos = null);
