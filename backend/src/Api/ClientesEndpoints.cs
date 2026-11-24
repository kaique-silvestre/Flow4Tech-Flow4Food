using Flow4Tech.Clientes.Application;
using Flow4Tech.Clientes.Domain;

namespace Flow4Tech.Api;

/// <summary>
/// The consent the customer gave. <c>Concedido</c> must be true to register (an explicit act of the person, never a default). <c>Finalidade</c> is a purpose code (lower case, digits and hyphens, 3 to 40)
/// and <c>VersaoDoTexto</c> identifies the text the customer accepted (1 to 20 characters).
/// </summary>
internal sealed record ConsentimentoInformadoDto(bool Concedido, string? Finalidade, string? VersaoDoTexto);

/// <summary>
/// <c>ConfirmarDuplicidade</c> is false until the caller saw the 409 <c>possivel-duplicidade</c> warning and decided to register anyway. Phone (10 or 11 digits, a leading 55 is dropped), e-mail and document
/// (11 or 14 digits) are optional; blank means absent.
/// </summary>
internal sealed record CadastrarClienteRequest(string? Nome, string? Telefone, string? Email, string? Documento, ConsentimentoInformadoDto? Consentimento, bool ConfirmarDuplicidade = false);

/// <summary>Replaces the name and the contacts (blank clears a contact). The consent is not edited here.</summary>
internal sealed record EditarClienteRequest(string? Nome, string? Telefone, string? Email, string? Documento, bool ConfirmarDuplicidade = false);

internal sealed record ConsentimentoDoClienteDto(bool Concedido, DateTimeOffset? Quando, string? Finalidade, string? VersaoDoTexto, Guid? RegistradoPor, DateTimeOffset? RevogadoEm);

/// <summary>
/// A customer. When <c>DadosMascarados</c> is true the caller lacks <c>cliente:ver-dados-pessoais</c> and <c>Telefone</c>, <c>Email</c> and <c>Documento</c> are masked (<c>***-**12</c>, <c>j***@***</c>); <c>Nome</c> never is.
/// <c>Estado</c> is <c>ativo</c> or <c>anonimizado</c>.
/// </summary>
internal sealed record ClienteDto(Guid Id, string Nome, string? Telefone, string? Email, string? Documento, bool DadosMascarados, ConsentimentoDoClienteDto Consentimento, string Estado, DateTimeOffset CriadoEm);

/// <summary>Codes: <c>invalido</c>, <c>consentimento-obrigatorio</c> (400), <c>nao-encontrado</c> (404), <c>possivel-duplicidade</c> (409, with <c>Candidatos</c>), <c>anonimizado</c>, <c>consentimento-revogado</c>, <c>consentimento-ja-revogado</c> (409).</summary>
internal sealed record ClienteErro(string Codigo, List<ClienteDto>? Candidatos = null);

internal static class ClientesEndpoints
{
    private const long Body = 4096;

    public static void MapClientes(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/clientes", async (string? busca, RequestContextAccessor request, GestaoDeClientes gestao, CancellationToken cancellationToken) =>
                Results.Ok((await gestao.BuscarAsync(request, busca, cancellationToken)).Select(Dto).ToList()))
            .WithName("listClientes")
            .RequireSession()
            .RequirePermission(ClientesPermissions.Ver)
            .Produces<List<ClienteDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        app.MapGet("/clientes/{clienteId:guid}", async (Guid clienteId, RequestContextAccessor request, GestaoDeClientes gestao, CancellationToken cancellationToken) =>
            {
                var cliente = await gestao.ObterAsync(request, clienteId, cancellationToken);
                return cliente is null ? Erro(new ResultadoDeCliente(ResultadoDoCliente.NaoEncontrado)) : Results.Ok(Dto(cliente));
            })
            .WithName("getCliente")
            .RequireSession()
            .RequirePermission(ClientesPermissions.Ver)
            .Produces<ClienteDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ClienteErro>(StatusCodes.Status404NotFound);

        app.MapPost("/clientes", async (CadastrarClienteRequest body, RequestContextAccessor request, GestaoDeClientes gestao, CancellationToken cancellationToken) =>
            {
                var consentimento = body.Consentimento is null ? null : new ConsentimentoInformado(body.Consentimento.Concedido, body.Consentimento.Finalidade, body.Consentimento.VersaoDoTexto);
                var r = await gestao.CadastrarAsync(request, new DadosDoCliente(body.Nome, body.Telefone, body.Email, body.Documento), consentimento, body.ConfirmarDuplicidade, cancellationToken);
                return r.Resultado == ResultadoDoCliente.Ok ? Results.Json(Dto(r.Cliente!), statusCode: StatusCodes.Status201Created) : Erro(r);
            })
            .WithName("cadastrarCliente")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ClientesPermissions.Cadastrar)
            .Produces<ClienteDto>(StatusCodes.Status201Created)
            .Produces<ClienteErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ClienteErro>(StatusCodes.Status409Conflict);

        app.MapPut("/clientes/{clienteId:guid}", async (Guid clienteId, EditarClienteRequest body, RequestContextAccessor request, GestaoDeClientes gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.EditarAsync(request, clienteId, new DadosDoCliente(body.Nome, body.Telefone, body.Email, body.Documento), body.ConfirmarDuplicidade, cancellationToken);
                return r.Resultado == ResultadoDoCliente.Ok ? Results.Ok(Dto(r.Cliente!)) : Erro(r);
            })
            .WithName("editarCliente")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ClientesPermissions.Editar)
            .Produces<ClienteDto>()
            .Produces<ClienteErro>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ClienteErro>(StatusCodes.Status404NotFound)
            .Produces<ClienteErro>(StatusCodes.Status409Conflict);

        app.MapPost("/clientes/{clienteId:guid}/revogar-consentimento", async (Guid clienteId, RequestContextAccessor request, GestaoDeClientes gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.RevogarConsentimentoAsync(request, clienteId, cancellationToken);
                return r.Resultado == ResultadoDoCliente.Ok ? Results.Ok(Dto(r.Cliente!)) : Erro(r);
            })
            .WithName("revogarConsentimentoDoCliente")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ClientesPermissions.Editar)
            .Produces<ClienteDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ClienteErro>(StatusCodes.Status404NotFound)
            .Produces<ClienteErro>(StatusCodes.Status409Conflict);

        app.MapPost("/clientes/{clienteId:guid}/anonimizar", async (Guid clienteId, RequestContextAccessor request, GestaoDeClientes gestao, CancellationToken cancellationToken) =>
            {
                var r = await gestao.AnonimizarAsync(request, clienteId, cancellationToken);
                return r.Resultado == ResultadoDoCliente.Ok ? Results.Ok(Dto(r.Cliente!)) : Erro(r);
            })
            .WithName("anonimizarCliente")
            .RequireSession()
            .WithBodyLimit(Body)
            .RequirePermission(ClientesPermissions.Anonimizar, sensitive: true)
            .Produces<ClienteDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces<ClienteErro>(StatusCodes.Status404NotFound)
            .Produces<ClienteErro>(StatusCodes.Status409Conflict);
    }

    private static ClienteDto Dto(ClienteExibido c) =>
        new(
            c.Id,
            c.Nome,
            c.Telefone,
            c.Email,
            c.Documento,
            c.DadosMascarados,
            new ConsentimentoDoClienteDto(c.Consentimento.Concedido, c.Consentimento.Quando, c.Consentimento.Finalidade, c.Consentimento.VersaoDoTexto, c.Consentimento.RegistradoPor, c.Consentimento.RevogadoEm),
            c.Estado == EstadoDoCliente.Ativo ? "ativo" : "anonimizado",
            c.CriadoEm);

    private static IResult Erro(ResultadoDeCliente r)
    {
        var (status, codigo) = r.Resultado switch
        {
            ResultadoDoCliente.NaoEncontrado => (StatusCodes.Status404NotFound, "nao-encontrado"),
            ResultadoDoCliente.PossivelDuplicidade => (StatusCodes.Status409Conflict, "possivel-duplicidade"),
            ResultadoDoCliente.Anonimizado => (StatusCodes.Status409Conflict, "anonimizado"),
            ResultadoDoCliente.ConsentimentoRevogado => (StatusCodes.Status409Conflict, "consentimento-revogado"),
            ResultadoDoCliente.ConsentimentoJaRevogado => (StatusCodes.Status409Conflict, "consentimento-ja-revogado"),
            ResultadoDoCliente.ConsentimentoObrigatorio => (StatusCodes.Status400BadRequest, "consentimento-obrigatorio"),
            _ => (StatusCodes.Status400BadRequest, "invalido"),
        };
        return Results.Json(new ClienteErro(codigo, r.Candidatos?.Select(Dto).ToList()), statusCode: status);
    }
}
