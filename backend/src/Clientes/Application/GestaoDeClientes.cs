using System.Text.Json.Nodes;
using Flow4Tech.Clientes.Domain;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Audit;

namespace Flow4Tech.Clientes.Application;

/// <summary>
/// The customers of the organization (ADR-017). The route has already asked the engine for the plain permission of the action; whether the personal data comes back masked is decided here with
/// <c>cliente:ver-dados-pessoais</c>. Every write is ONE unit of work with its audit record, and the audit holds only ids, flags and field NAMES: never a name, a contact or a document.
/// Registering needs the consent granted and, when another customer shares the phone, the e-mail or the document, the caller's explicit confirmation (the warning lists the candidates, masked like
/// everything else). The duplicate check is a warning, not a constraint: two simultaneous confirmed registrations can both pass.
/// </summary>
public sealed class GestaoDeClientes(IUnitOfWork unitOfWork, IClientesStore clientes, IAuditLog audit, IPermissaoDeCliente permissoes, IClock clock)
{
    public const int LimiteDaBusca = 50;

    public Task<ResultadoDeCliente> CadastrarAsync(IRequestContext request, DadosDoCliente dados, ConsentimentoInformado? consentimento, bool confirmarDuplicidade, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dados);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!Normalizar(dados, out var nome, out var telefone, out var email, out var documento))
                {
                    return new ResultadoDeCliente(ResultadoDoCliente.Invalido);
                }

                if (consentimento is not { Concedido: true })
                {
                    return new ResultadoDeCliente(ResultadoDoCliente.ConsentimentoObrigatorio);
                }

                if (!RegrasDeCliente.FinalidadeValida(consentimento.Finalidade) || !RegrasDeCliente.VersaoValida(consentimento.VersaoDoTexto))
                {
                    return new ResultadoDeCliente(ResultadoDoCliente.Invalido);
                }

                var org = request.Organization.OrganizationId;
                var verDados = await permissoes.PodeAsync(request, ClientesPermissions.VerDadosPessoais, ct);
                var candidatos = await clientes.AcharDuplicadosAsync(org, telefone, email, documento, null, ct);
                if (candidatos.Count > 0 && !confirmarDuplicidade)
                {
                    return new ResultadoDeCliente(ResultadoDoCliente.PossivelDuplicidade, Candidatos: [.. candidatos.Select(c => ClienteExibido.De(c, verDados))]);
                }

                var agora = clock.UtcNow;
                var cliente = new Cliente(EntityId.New().Value, nome!, telefone, email, documento, new Consentimento(true, agora, consentimento.Finalidade, consentimento.VersaoDoTexto, request.UserId, null), EstadoDoCliente.Ativo, agora);
                await clientes.InserirAsync(org, cliente, ct);
                var depois = new JsonObject
                {
                    ["consentimento"] = true,
                    ["duplicidadeConfirmada"] = candidatos.Count > 0,
                    ["comTelefone"] = telefone is not null,
                    ["comEmail"] = email is not null,
                    ["comDocumento"] = documento is not null,
                };
                await audit.RecordAsync(request, new AuditEntry { Action = ClientesAuditActions.Cadastrar, ObjectType = "cliente", ObjectId = cliente.Id.ToString("D"), After = depois }, ct);
                return new ResultadoDeCliente(ResultadoDoCliente.Ok, ClienteExibido.De(cliente, verDados));
            },
            cancellationToken);
    }

    /// <summary>Replaces the name and the contacts (blank contacts clear them). A contact that CHANGED and now matches another customer is a possible duplicate, confirmed the same way.</summary>
    public Task<ResultadoDeCliente> EditarAsync(IRequestContext request, Guid clienteId, DadosDoCliente dados, bool confirmarDuplicidade, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dados);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                if (!Normalizar(dados, out var nome, out var telefone, out var email, out var documento))
                {
                    return new ResultadoDeCliente(ResultadoDoCliente.Invalido);
                }

                var org = request.Organization.OrganizationId;
                var antes = await clientes.AcharAsync(org, clienteId, ct);
                var recusa = Recusa(antes);
                if (recusa is not null)
                {
                    return new ResultadoDeCliente(recusa.Value);
                }

                var verDados = await permissoes.PodeAsync(request, ClientesPermissions.VerDadosPessoais, ct);
                var candidatos = await clientes.AcharDuplicadosAsync(org, telefone == antes!.Telefone ? null : telefone, email == antes.Email ? null : email, documento == antes.Documento ? null : documento, clienteId, ct);
                if (candidatos.Count > 0 && !confirmarDuplicidade)
                {
                    return new ResultadoDeCliente(ResultadoDoCliente.PossivelDuplicidade, Candidatos: [.. candidatos.Select(c => ClienteExibido.De(c, verDados))]);
                }

                if (!await clientes.AtualizarAsync(org, clienteId, nome!, telefone, email, documento, ct))
                {
                    throw new InvalidOperationException("O cliente mudou durante a edicao.");
                }

                var campos = new JsonArray();
                foreach (var (campo, mudou) in new[] { ("nome", nome != antes.Nome), ("telefone", telefone != antes.Telefone), ("email", email != antes.Email), ("documento", documento != antes.Documento) })
                {
                    if (mudou)
                    {
                        campos.Add(campo);
                    }
                }

                await audit.RecordAsync(request, new AuditEntry { Action = ClientesAuditActions.Editar, ObjectType = "cliente", ObjectId = clienteId.ToString("D"), After = new JsonObject { ["campos"] = campos, ["duplicidadeConfirmada"] = candidatos.Count > 0 } }, ct);
                return new ResultadoDeCliente(ResultadoDoCliente.Ok, ClienteExibido.De(antes with { Nome = nome!, Telefone = telefone, Email = email, Documento = documento }, verDados));
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<ClienteExibido>> BuscarAsync(IRequestContext request, string? texto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync<IReadOnlyList<ClienteExibido>>(
            request,
            async ct =>
            {
                var verDados = await permissoes.PodeAsync(request, ClientesPermissions.VerDadosPessoais, ct);
                return [.. (await clientes.BuscarAsync(request.Organization.OrganizationId, texto?.Trim(), LimiteDaBusca, ct)).Select(c => ClienteExibido.De(c, verDados))];
            },
            cancellationToken);
    }

    public Task<ClienteExibido?> ObterAsync(IRequestContext request, Guid clienteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var cliente = await clientes.AcharAsync(request.Organization.OrganizationId, clienteId, ct);
                return cliente is null ? null : ClienteExibido.De(cliente, await permissoes.PodeAsync(request, ClientesPermissions.VerDadosPessoais, ct));
            },
            cancellationToken);
    }

    /// <summary>Revokes the consent: the customer stays on file but is no longer linked to anything new and its data is no longer edited. Anonymizing is the way to erase the data.</summary>
    public Task<ResultadoDeCliente> RevogarConsentimentoAsync(IRequestContext request, Guid clienteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization.OrganizationId;
                var cliente = await clientes.AcharAsync(org, clienteId, ct);
                if (cliente is null)
                {
                    return new ResultadoDeCliente(ResultadoDoCliente.NaoEncontrado);
                }

                if (cliente.Estado == EstadoDoCliente.Anonimizado)
                {
                    return new ResultadoDeCliente(ResultadoDoCliente.Anonimizado);
                }

                if (!await clientes.RevogarConsentimentoAsync(org, clienteId, clock.UtcNow, ct))
                {
                    return new ResultadoDeCliente(ResultadoDoCliente.ConsentimentoJaRevogado);
                }

                await audit.RecordAsync(request, new AuditEntry { Action = ClientesAuditActions.RevogarConsentimento, ObjectType = "cliente", ObjectId = clienteId.ToString("D"), After = new JsonObject { ["consentimento"] = false } }, ct);
                var atual = (await clientes.AcharAsync(org, clienteId, ct))!;
                return new ResultadoDeCliente(ResultadoDoCliente.Ok, ClienteExibido.De(atual, await permissoes.PodeAsync(request, ClientesPermissions.VerDadosPessoais, ct)));
            },
            cancellationToken);
    }

    /// <summary>Anonymizes ("forgets") the customer: name and contacts are erased, the row (id, consent proof, links to comandas and payments) stays. Irreversible.</summary>
    public Task<ResultadoDeCliente> AnonimizarAsync(IRequestContext request, Guid clienteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return unitOfWork.ExecuteAsync(
            request,
            async ct =>
            {
                var org = request.Organization.OrganizationId;
                var cliente = await clientes.AcharAsync(org, clienteId, ct);
                if (cliente is null)
                {
                    return new ResultadoDeCliente(ResultadoDoCliente.NaoEncontrado);
                }

                if (!await clientes.AnonimizarAsync(org, clienteId, clock.UtcNow, ct))
                {
                    return new ResultadoDeCliente(ResultadoDoCliente.Anonimizado);
                }

                var apagados = new JsonArray("nome");
                foreach (var (campo, tinha) in new[] { ("telefone", cliente.Telefone is not null), ("email", cliente.Email is not null), ("documento", cliente.Documento is not null) })
                {
                    if (tinha)
                    {
                        apagados.Add(campo);
                    }
                }

                await audit.RecordAsync(request, new AuditEntry { Action = ClientesAuditActions.Anonimizar, ObjectType = "cliente", ObjectId = clienteId.ToString("D"), After = new JsonObject { ["estado"] = "anonimizado", ["camposApagados"] = apagados } }, ct);
                var atual = (await clientes.AcharAsync(org, clienteId, ct))!;
                return new ResultadoDeCliente(ResultadoDoCliente.Ok, ClienteExibido.De(atual, await permissoes.PodeAsync(request, ClientesPermissions.VerDadosPessoais, ct)));
            },
            cancellationToken);
    }

    private static ResultadoDoCliente? Recusa(Cliente? cliente)
    {
        if (cliente is null)
        {
            return ResultadoDoCliente.NaoEncontrado;
        }

        if (cliente.Estado == EstadoDoCliente.Anonimizado)
        {
            return ResultadoDoCliente.Anonimizado;
        }

        return cliente.Consentimento.Vigente ? null : ResultadoDoCliente.ConsentimentoRevogado;
    }

    /// <summary>Normalizes the fields; false when the name is invalid or a filled contact is malformed. A blank contact is null.</summary>
    private static bool Normalizar(DadosDoCliente dados, out string? nome, out string? telefone, out string? email, out string? documento)
    {
        nome = RegrasDeCliente.NomeValido(dados.Nome);
        telefone = email = documento = null;
        if (nome is null)
        {
            return false;
        }

        if (!RegrasDeCliente.Ausente(dados.Telefone) && (telefone = RegrasDeCliente.Telefone(dados.Telefone)) is null)
        {
            return false;
        }

        if (!RegrasDeCliente.Ausente(dados.Email) && (email = RegrasDeCliente.Email(dados.Email)) is null)
        {
            return false;
        }

        return RegrasDeCliente.Ausente(dados.Documento) || (documento = RegrasDeCliente.Documento(dados.Documento)) is not null;
    }
}
