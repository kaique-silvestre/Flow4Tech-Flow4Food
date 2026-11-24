using System.Net;
using System.Net.Http.Json;
using Flow4Tech.Clientes.Infrastructure;
using Flow4Tech.Infrastructure.Data;
using Flow4Tech.Kernel;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0147: the customers module through the real authenticated routes (real PostgreSQL, application credential with RLS, real sessions): registering with the LGPD consent, the duplicate warning and its explicit
/// confirmation, masking of phone, e-mail and document without <c>cliente:ver-dados-pessoais</c>, the optional link to a comanda, revoking the consent, anonymizing, audit without personal data and two organizations.
/// The owner holds every permission; "Mudo" can see, register, edit and link customers but not their personal data; "Supervisor" can see customers with the personal data; "Garcom" holds no customer permission.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private sealed record ConsentimentoBody(bool Concedido, DateTimeOffset? Quando, string? Finalidade, string? VersaoDoTexto, Guid? RegistradoPor, DateTimeOffset? RevogadoEm);

    private sealed record ClienteBody(Guid Id, string Nome, string? Telefone, string? Email, string? Documento, bool DadosMascarados, ConsentimentoBody Consentimento, string Estado, DateTimeOffset CriadoEm);

    private sealed record ClienteErroBody(string Codigo, List<ClienteBody>? Candidatos);

    private sealed record ComandaComClienteBody(Guid Id, int Versao, Guid? ClienteId);

    private static object NovoCliente(string nome, string? telefone = null, string? email = null, string? documento = null, bool concedido = true, bool confirmar = false) =>
        new { nome, telefone, email, documento, consentimento = new { concedido, finalidade = "relacionamento", versaoDoTexto = "v1" }, confirmarDuplicidade = confirmar };

    private static async Task<ClienteBody> RegisterCustomerAsync(HttpClient client, string token, object body) => await BodyAsync<ClienteBody>(await SendAsync(client, HttpMethod.Post, "/clientes", token, body), HttpStatusCode.Created);

    private static async Task AssertCustomerErrorAsync(HttpResponseMessage response, HttpStatusCode status, string codigo)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(codigo, (await response.Content.ReadFromJsonAsync<ClienteErroBody>())!.Codigo);
        }
    }

    private async Task<string> GiveCustomerProfileAsync(Api api, Guid user, string nome, params string[] permissoes)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var perfil = await perfis.CriarAsync(request, nome, [.. permissoes.Select(p => new RegraDeAcesso(RuleEffect.Allow, p))]);
        Assert.Equal(ResultadoGestao.Ok, perfil.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, user, perfil.Id!.Value));
        return await FreshTokenAsync(api, user);
    }

    /// <summary>Mudo: everything about customers except the personal data and anonymizing; may also see the comandas of other waiters (to link them).</summary>
    private Task<string> GiveMaskedCustomerProfileAsync(Api api) => GiveCustomerProfileAsync(api, Mudo, "Atendente de clientes", "cliente:ver", "cliente:cadastrar", "cliente:editar", "cliente:vincular", "comanda:ver", "comanda:ver-comandas-de-outros");

    private Task<long> CustomerRowsAsync() => CountAsync("clientes");

    [Fact]
    public async Task Registering_with_consent_normalizes_stores_the_proof_and_audits_without_any_personal_data()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var cliente = await RegisterCustomerAsync(client, t.Dono, NovoCliente("  Maria Souza ", "(11) 98765-4321", " Maria@Exemplo.com ", "123.456.789-01"));

        Assert.Equal(("Maria Souza", "11987654321", "maria@exemplo.com", "12345678901", false, "ativo"), (cliente.Nome, cliente.Telefone, cliente.Email, cliente.Documento, cliente.DadosMascarados, cliente.Estado));
        Assert.Equal((true, "relacionamento", "v1", DonoA, (DateTimeOffset?)null), (cliente.Consentimento.Concedido, cliente.Consentimento.Finalidade, cliente.Consentimento.VersaoDoTexto, cliente.Consentimento.RegistradoPor, cliente.Consentimento.RevogadoEm));
        var row = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT nome, telefone, email, documento, consentimento_concedido, consentimento_por, consentimento_em IS NOT NULL, estado FROM clientes WHERE id = '{cliente.Id:D}'", r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetGuid(5), r.GetBoolean(6), r.GetString(7))));
        Assert.Equal(("Maria Souza", "11987654321", "maria@exemplo.com", "12345678901", true, DonoA, true, "ativo"), row);

        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, resultado, objeto_tipo, objeto_id, depois::text FROM auditoria_registros WHERE acao = 'cliente:cadastrar'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))));
        Assert.Equal((DonoA, "sucesso", "cliente", cliente.Id.ToString("D")), (audit.Item1, audit.Item2, audit.Item3, audit.Item4));
        Assert.DoesNotContain("Maria", audit.Item5, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("98765", audit.Item5, StringComparison.Ordinal);
        Assert.DoesNotContain("exemplo", audit.Item5, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12345678901", audit.Item5, StringComparison.Ordinal);

        // A customer needs only a name and the consent: no contact at all is fine.
        var minimo = await RegisterCustomerAsync(client, t.Dono, NovoCliente("So o nome"));
        Assert.Equal((null, null, null), (minimo.Telefone, minimo.Email, minimo.Documento));
    }

    [Fact]
    public async Task Registering_without_an_explicit_granted_consent_or_with_an_invalid_field_is_400_and_writes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, "/clientes", t.Dono, NovoCliente("Maria", concedido: false)), HttpStatusCode.BadRequest, "consentimento-obrigatorio");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, "/clientes", t.Dono, new { nome = "Maria", telefone = "11987654321" }), HttpStatusCode.BadRequest, "consentimento-obrigatorio");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, "/clientes", t.Dono, new { nome = "Maria", consentimento = (object?)null }), HttpStatusCode.BadRequest, "consentimento-obrigatorio");

        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, "/clientes", t.Dono, NovoCliente("   ")), HttpStatusCode.BadRequest, "invalido");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, "/clientes", t.Dono, NovoCliente("Maria", telefone: "123")), HttpStatusCode.BadRequest, "invalido");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, "/clientes", t.Dono, NovoCliente("Maria", email: "sem-arroba")), HttpStatusCode.BadRequest, "invalido");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, "/clientes", t.Dono, NovoCliente("Maria", documento: "123")), HttpStatusCode.BadRequest, "invalido");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, "/clientes", t.Dono, new { nome = "Maria", consentimento = new { concedido = true, finalidade = "Texto Livre", versaoDoTexto = "v1" } }), HttpStatusCode.BadRequest, "invalido");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, "/clientes", t.Dono, new { nome = "Maria", consentimento = new { concedido = true, finalidade = "relacionamento", versaoDoTexto = "" } }), HttpStatusCode.BadRequest, "invalido");

        Assert.Equal((0L, 0L), (await CustomerRowsAsync(), await CountAsync("auditoria_registros WHERE acao = 'cliente:cadastrar'")));
    }

    [Fact]
    public async Task The_same_phone_email_or_document_warns_with_the_candidates_and_registers_only_when_confirmed()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mudo = await GiveMaskedCustomerProfileAsync(api);
        var maria = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Maria Souza", "11987654321", "maria@exemplo.com", "12345678901"));

        // Same phone written another way, same e-mail in capitals, same document with punctuation: each one is a warning that names the candidate.
        foreach (var igual in new[] { NovoCliente("Outra Maria", telefone: "+55 (11) 98765-4321"), NovoCliente("Outra Maria", email: "MARIA@exemplo.COM"), NovoCliente("Outra Maria", documento: "123.456.789-01") })
        {
            using var aviso = await SendAsync(client, HttpMethod.Post, "/clientes", t.Dono, igual);
            Assert.Equal(HttpStatusCode.Conflict, aviso.StatusCode);
            var erro = (await aviso.Content.ReadFromJsonAsync<ClienteErroBody>())!;
            var candidato = Assert.Single(erro.Candidatos!);
            Assert.Equal(("possivel-duplicidade", maria.Id, "11987654321"), (erro.Codigo, candidato.Id, candidato.Telefone));
        }

        Assert.Equal(1L, await CustomerRowsAsync());

        // The candidates are masked for someone who may not see the personal data (the name is not).
        using var avisoMascarado = await SendAsync(client, HttpMethod.Post, "/clientes", mudo, NovoCliente("Outra Maria", telefone: "11987654321"));
        Assert.Equal(HttpStatusCode.Conflict, avisoMascarado.StatusCode);
        var mascarado = Assert.Single((await avisoMascarado.Content.ReadFromJsonAsync<ClienteErroBody>())!.Candidatos!);
        Assert.Equal(("Maria Souza", "***-**21", "m***@***", "***-**01", true), (mascarado.Nome, mascarado.Telefone, mascarado.Email, mascarado.Documento, mascarado.DadosMascarados));

        // No overlap: registers without confirmation. Confirmed overlap: registers and the audit says so.
        _ = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Joao", "11911112222"));
        var confirmada = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Outra Maria", "11987654321", confirmar: true));
        Assert.Equal(3L, await CustomerRowsAsync());
        Assert.Equal("true", (await QueryAsync(OrgA, UnitA, $"SELECT depois->>'duplicidadeConfirmada' FROM auditoria_registros WHERE acao = 'cliente:cadastrar' AND objeto_id = '{confirmada.Id:D}'", r => r.GetString(0))).Single());
        Assert.Equal(2L, await CountAsync("auditoria_registros WHERE acao = 'cliente:cadastrar' AND depois->>'duplicidadeConfirmada' = 'false'"));
    }

    [Fact]
    public async Task Without_cliente_ver_dados_pessoais_phone_email_and_document_are_masked_everywhere_and_the_name_never_is()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mudo = await GiveMaskedCustomerProfileAsync(api);
        var supervisor = await GiveCustomerProfileAsync(api, Supervisor, "Ve clientes", "cliente:ver", "cliente:ver-dados-pessoais");
        var maria = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Maria Souza", "11987654321", "maria@exemplo.com", "12345678901"));
        _ = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Joao Pereira", "11911112222"));

        // Who may see the data sees it in the list, the search and the detail.
        foreach (var token in new[] { t.Dono, supervisor })
        {
            var lista = await BodyAsync<List<ClienteBody>>(await SendAsync(client, HttpMethod.Get, "/clientes", token), HttpStatusCode.OK);
            Assert.Equal(["Joao Pereira", "Maria Souza"], lista.Select(c => c.Nome));
            Assert.Equal(("11987654321", "maria@exemplo.com", "12345678901", false), lista.Where(c => c.Id == maria.Id).Select(c => (c.Telefone, c.Email, c.Documento, c.DadosMascarados)).Single());
            var detalhe = await BodyAsync<ClienteBody>(await SendAsync(client, HttpMethod.Get, $"/clientes/{maria.Id:D}", token), HttpStatusCode.OK);
            Assert.Equal(("11987654321", false), (detalhe.Telefone, detalhe.DadosMascarados));
        }

        // Who may not gets the masks, with the name intact; the same on every route that returns a customer, including the one that registers.
        var mascarada = await BodyAsync<List<ClienteBody>>(await SendAsync(client, HttpMethod.Get, "/clientes", mudo), HttpStatusCode.OK);
        Assert.Equal(["Joao Pereira", "Maria Souza"], mascarada.Select(c => c.Nome));
        Assert.Equal(("***-**21", "m***@***", "***-**01", true), mascarada.Where(c => c.Id == maria.Id).Select(c => (c.Telefone, c.Email, c.Documento, c.DadosMascarados)).Single());
        Assert.Null(mascarada.Single(c => c.Nome == "Joao Pereira").Email);
        var detalheMascarado = await BodyAsync<ClienteBody>(await SendAsync(client, HttpMethod.Get, $"/clientes/{maria.Id:D}", mudo), HttpStatusCode.OK);
        Assert.Equal(("Maria Souza", "***-**21", true), (detalheMascarado.Nome, detalheMascarado.Telefone, detalheMascarado.DadosMascarados));
        var cadastrado = await RegisterCustomerAsync(client, mudo, NovoCliente("Ana Lima", "11922223333", "ana@exemplo.com"));
        Assert.Equal(("***-**33", "a***@***", true), (cadastrado.Telefone, cadastrado.Email, cadastrado.DadosMascarados));
        var editado = await BodyAsync<ClienteBody>(await SendAsync(client, HttpMethod.Put, $"/clientes/{cadastrado.Id:D}", mudo, new { nome = "Ana Lima Silva", telefone = "11922224444", email = "ana@exemplo.com" }), HttpStatusCode.OK);
        Assert.Equal(("Ana Lima Silva", "***-**44"), (editado.Nome, editado.Telefone));
        Assert.Equal("11922224444", (await QueryAsync(OrgA, UnitA, $"SELECT telefone FROM clientes WHERE id = '{cadastrado.Id:D}'", r => r.GetString(0))).Single());

        // Search by part of the name (any case) and by digits of the phone; a masked reader never learns the phone from the masked answer.
        Assert.Equal(["Maria Souza"], (await BodyAsync<List<ClienteBody>>(await SendAsync(client, HttpMethod.Get, "/clientes?busca=SOUZ", mudo), HttpStatusCode.OK)).Select(c => c.Nome));
        Assert.Equal(["Joao Pereira"], (await BodyAsync<List<ClienteBody>>(await SendAsync(client, HttpMethod.Get, "/clientes?busca=1111-22", t.Dono), HttpStatusCode.OK)).Select(c => c.Nome));
        Assert.Empty(await BodyAsync<List<ClienteBody>>(await SendAsync(client, HttpMethod.Get, "/clientes?busca=inexistente", t.Dono), HttpStatusCode.OK));
        Assert.Empty(await BodyAsync<List<ClienteBody>>(await SendAsync(client, HttpMethod.Get, "/clientes?busca=%25", t.Dono), HttpStatusCode.OK));
    }

    [Fact]
    public async Task Every_route_is_401_without_a_session_and_403_with_an_empty_body_without_its_permission_and_anonymizing_is_audited_as_a_denial()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mudo = await GiveMaskedCustomerProfileAsync(api);
        var maria = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Maria Souza", "11987654321"));
        var comanda = await OpenAsync(client, t.Garcom);
        var id = maria.Id.ToString("D");

        var rotas = new (HttpMethod Metodo, string Caminho, object? Corpo)[]
        {
            (HttpMethod.Get, "/clientes", null),
            (HttpMethod.Get, $"/clientes/{id}", null),
            (HttpMethod.Post, "/clientes", NovoCliente("Joao")),
            (HttpMethod.Put, $"/clientes/{id}", new { nome = "Maria" }),
            (HttpMethod.Post, $"/clientes/{id}/revogar-consentimento", null),
            (HttpMethod.Post, $"/clientes/{id}/anonimizar", null),
            (HttpMethod.Put, $"/comandas/{comanda.Id:D}/cliente", new { versao = comanda.Versao, clienteId = maria.Id }),
        };
        foreach (var (metodo, caminho, corpo) in rotas)
        {
            using var semSessao = await SendAsync(client, metodo, caminho, null, corpo);
            Assert.Equal(HttpStatusCode.Unauthorized, semSessao.StatusCode);
            using var semPermissao = await SendAsync(client, metodo, caminho, t.Garcom, corpo);
            Assert.Equal(HttpStatusCode.Forbidden, semPermissao.StatusCode);
            Assert.Equal(string.Empty, await semPermissao.Content.ReadAsStringAsync());
        }

        // Mudo may do the plain actions but not anonymize.
        using var negado = await SendAsync(client, HttpMethod.Post, $"/clientes/{id}/anonimizar", mudo);
        Assert.Equal((HttpStatusCode.Forbidden, string.Empty), (negado.StatusCode, await negado.Content.ReadAsStringAsync()));
        Assert.Equal("ativo", (await QueryAsync(OrgA, UnitA, $"SELECT estado FROM clientes WHERE id = '{id}'", r => r.GetString(0))).Single());
        var negacao = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT usuario_id, depois->>'permissao' FROM auditoria_registros WHERE acao = 'acesso:negar' AND resultado = 'negado' AND usuario_id = '{Mudo:D}'", r => (r.GetGuid(0), r.GetString(1))));
        Assert.Equal((Mudo, "cliente:anonimizar"), negacao);
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'cliente:anonimizar'"));
        Assert.Equal(1L, await CustomerRowsAsync());
    }

    [Fact]
    public async Task Another_organization_neither_sees_changes_anonymizes_nor_links_the_customer()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var deA = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Maria de A", "11987654321", "maria@a.test"));
        var deB = await RegisterCustomerAsync(client, t.DonoB, NovoCliente("Joao de B", "11911112222"));

        // Each organization lists only its own, and the same phone in the other one is no duplicate.
        Assert.Equal(["Maria de A"], (await BodyAsync<List<ClienteBody>>(await SendAsync(client, HttpMethod.Get, "/clientes", t.Dono), HttpStatusCode.OK)).Select(c => c.Nome));
        Assert.Equal(["Joao de B"], (await BodyAsync<List<ClienteBody>>(await SendAsync(client, HttpMethod.Get, "/clientes", t.DonoB), HttpStatusCode.OK)).Select(c => c.Nome));
        _ = await RegisterCustomerAsync(client, t.DonoB, NovoCliente("Mesmo telefone em B", "11987654321"));

        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Get, $"/clientes/{deA.Id:D}", t.DonoB), HttpStatusCode.NotFound, "nao-encontrado");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Put, $"/clientes/{deA.Id:D}", t.DonoB, new { nome = "Invadido" }), HttpStatusCode.NotFound, "nao-encontrado");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, $"/clientes/{deA.Id:D}/revogar-consentimento", t.DonoB), HttpStatusCode.NotFound, "nao-encontrado");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, $"/clientes/{deA.Id:D}/anonimizar", t.DonoB), HttpStatusCode.NotFound, "nao-encontrado");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Get, $"/clientes/{Guid.NewGuid():D}", t.Dono), HttpStatusCode.NotFound, "nao-encontrado");
        Assert.Equal(("Maria de A", "ativo"), (await QueryAsync(OrgA, UnitA, $"SELECT nome, estado FROM clientes WHERE id = '{deA.Id:D}'", r => (r.GetString(0), r.GetString(1)))).Single());
        Assert.Empty(await QueryAsync(OrgB, UnitB, $"SELECT 1 FROM clientes WHERE id = '{deA.Id:D}'", r => r.GetInt32(0)));

        // Linking: a comanda of B takes no customer of A, and a comanda of A takes no customer of B.
        var comandaB = await OpenAsync(client, t.DonoB);
        var comandaA = await OpenAsync(client, t.Garcom);
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, $"/comandas/{comandaB.Id:D}/cliente", t.DonoB, new { versao = comandaB.Versao, clienteId = deA.Id }), HttpStatusCode.NotFound, "cliente-nao-encontrado");
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, $"/comandas/{comandaA.Id:D}/cliente", t.Dono, new { versao = comandaA.Versao, clienteId = deB.Id }), HttpStatusCode.NotFound, "cliente-nao-encontrado");
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, $"/comandas/{comandaA.Id:D}/cliente", t.DonoB, new { versao = comandaA.Versao, clienteId = deB.Id }), HttpStatusCode.NotFound, "nao-encontrada");
        Assert.Equal(0L, await CountAsync("comandas WHERE cliente_id IS NOT NULL"));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'comanda:vincular-cliente'"));
    }

    [Fact]
    public async Task A_customer_is_linked_to_an_open_comanda_with_the_version_check_unlinked_and_refused_when_anonymized_or_revoked()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var mudo = await GiveMaskedCustomerProfileAsync(api);
        var maria = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Maria Souza", "11987654321"));
        var comanda = await OpenAsync(client, t.Garcom);

        var vinculada = await BodyAsync<ComandaComClienteBody>(await SendAsync(client, HttpMethod.Put, $"/comandas/{comanda.Id:D}/cliente", mudo, new { versao = comanda.Versao, clienteId = maria.Id }), HttpStatusCode.OK);
        Assert.Equal((comanda.Versao + 1, maria.Id), (vinculada.Versao, vinculada.ClienteId));
        Assert.Equal(maria.Id, (await QueryAsync(OrgA, UnitA, $"SELECT cliente_id FROM comandas WHERE id = '{comanda.Id:D}'", r => r.GetGuid(0))).Single());
        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_id, depois->>'clienteId', antes->>'clienteId', depois::text FROM auditoria_registros WHERE acao = 'comanda:vincular-cliente'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4))));
        Assert.Equal((Mudo, comanda.Id.ToString("D"), maria.Id.ToString("D"), (string?)null), (audit.Item1, audit.Item2, audit.Item3, audit.Item4));
        Assert.DoesNotContain("Maria", audit.Item5, StringComparison.Ordinal);

        // A stale version changes nothing; the current one unlinks (null) and bumps again.
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, $"/comandas/{comanda.Id:D}/cliente", mudo, new { versao = comanda.Versao, clienteId = (Guid?)null }), HttpStatusCode.Conflict, "versao-desatualizada", vinculada.Versao);
        Assert.Equal(maria.Id, (await QueryAsync(OrgA, UnitA, $"SELECT cliente_id FROM comandas WHERE id = '{comanda.Id:D}'", r => r.GetGuid(0))).Single());
        var solta = await BodyAsync<ComandaComClienteBody>(await SendAsync(client, HttpMethod.Put, $"/comandas/{comanda.Id:D}/cliente", mudo, new { versao = vinculada.Versao, clienteId = (Guid?)null }), HttpStatusCode.OK);
        Assert.Equal((vinculada.Versao + 1, (Guid?)null), (solta.Versao, solta.ClienteId));

        // A customer that does not exist, an empty id, a closed comanda, an anonymized customer and one that revoked the consent are all refused with a typed code.
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, $"/comandas/{comanda.Id:D}/cliente", mudo, new { versao = solta.Versao, clienteId = Guid.NewGuid() }), HttpStatusCode.NotFound, "cliente-nao-encontrado");
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, $"/comandas/{comanda.Id:D}/cliente", mudo, new { versao = solta.Versao, clienteId = Guid.Empty }), HttpStatusCode.BadRequest, "invalido");
        var revogado = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Revogou"));
        var esquecido = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Esquecido"));
        (await SendAsync(client, HttpMethod.Post, $"/clientes/{revogado.Id:D}/revogar-consentimento", t.Dono)).Dispose();
        (await SendAsync(client, HttpMethod.Post, $"/clientes/{esquecido.Id:D}/anonimizar", t.Dono)).Dispose();
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, $"/comandas/{comanda.Id:D}/cliente", mudo, new { versao = solta.Versao, clienteId = revogado.Id }), HttpStatusCode.Conflict, "cliente-indisponivel");
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, $"/comandas/{comanda.Id:D}/cliente", mudo, new { versao = solta.Versao, clienteId = esquecido.Id }), HttpStatusCode.Conflict, "cliente-indisponivel");
        await RunSqlAsync(OrgA, UnitA, $"UPDATE comandas SET estado = 'cancelada', fechada_em = now(), motivo_cancelamento = 'teste', cancelada_por = '{DonoA:D}' WHERE id = '{comanda.Id:D}'");
        await AssertErrorAsync(await SendAsync(client, HttpMethod.Put, $"/comandas/{comanda.Id:D}/cliente", mudo, new { versao = solta.Versao, clienteId = maria.Id }), HttpStatusCode.Conflict, "comanda-nao-aberta");
        Assert.Equal(2L, await CountAsync("auditoria_registros WHERE acao = 'comanda:vincular-cliente'"));
    }

    [Fact]
    public async Task Revoking_the_consent_keeps_the_customer_but_blocks_edits_and_new_links_and_is_audited_once()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var maria = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Maria Souza", "11987654321", "maria@exemplo.com"));

        // Editing before the revocation: only the fields that changed are named in the audit, never their values.
        var editada = await BodyAsync<ClienteBody>(await SendAsync(client, HttpMethod.Put, $"/clientes/{maria.Id:D}", t.Dono, new { nome = "Maria S. Souza", telefone = "(11) 98765-4321", email = "nova@exemplo.com" }), HttpStatusCode.OK);
        Assert.Equal(("Maria S. Souza", "11987654321", "nova@exemplo.com", null), (editada.Nome, editada.Telefone, editada.Email, editada.Documento));
        var campos = (await QueryAsync(OrgA, UnitA, "SELECT (depois->'campos')::text FROM auditoria_registros WHERE acao = 'cliente:editar'", r => r.GetString(0))).Single();
        Assert.Equal("[\"nome\",\"email\"]", campos.Replace(" ", string.Empty, StringComparison.Ordinal));
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Put, $"/clientes/{maria.Id:D}", t.Dono, new { nome = "" }), HttpStatusCode.BadRequest, "invalido");

        // A change that lands on another customer's contact is a possible duplicate; keeping its own phone is not.
        var outro = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Joao", "11911112222"));
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Put, $"/clientes/{outro.Id:D}", t.Dono, new { nome = "Joao", telefone = "11987654321" }), HttpStatusCode.Conflict, "possivel-duplicidade");
        Assert.Equal("Joao P.", (await BodyAsync<ClienteBody>(await SendAsync(client, HttpMethod.Put, $"/clientes/{outro.Id:D}", t.Dono, new { nome = "Joao P.", telefone = "11911112222" }), HttpStatusCode.OK)).Nome);

        var revogada = await BodyAsync<ClienteBody>(await SendAsync(client, HttpMethod.Post, $"/clientes/{maria.Id:D}/revogar-consentimento", t.Dono), HttpStatusCode.OK);
        Assert.False(revogada.Consentimento.RevogadoEm is null);
        Assert.Equal(("Maria S. Souza", "ativo", true), (revogada.Nome, revogada.Estado, revogada.Consentimento.Concedido));
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, $"/clientes/{maria.Id:D}/revogar-consentimento", t.Dono), HttpStatusCode.Conflict, "consentimento-ja-revogado");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Put, $"/clientes/{maria.Id:D}", t.Dono, new { nome = "Maria Nova" }), HttpStatusCode.Conflict, "consentimento-revogado");
        Assert.Equal("Maria S. Souza", (await QueryAsync(OrgA, UnitA, $"SELECT nome FROM clientes WHERE id = '{maria.Id:D}'", r => r.GetString(0))).Single());
        Assert.Equal(1L, await CountAsync("auditoria_registros WHERE acao = 'cliente:revogar-consentimento'"));
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, $"/clientes/{Guid.NewGuid():D}/revogar-consentimento", t.Dono), HttpStatusCode.NotFound, "nao-encontrado");
    }

    [Fact]
    public async Task Anonymizing_erases_the_personal_data_keeps_the_id_the_comanda_link_and_the_payment_history_and_is_audited_without_the_data()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var maria = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Maria Souza", "11987654321", "maria@exemplo.com", "12345678901"));
        _ = await OpenShiftAsync(client, t.Dono, 0);
        var (comanda, versao, _) = await ComandaWithConsumoAsync(client, t, 1000, 2, "CLI1");
        var vinculada = await BodyAsync<ComandaComClienteBody>(await SendAsync(client, HttpMethod.Put, $"/comandas/{comanda:D}/cliente", t.Dono, new { versao, clienteId = maria.Id }), HttpStatusCode.OK);
        var pago = await PayOkAsync(client, t.Dono, comanda, 2200, vinculada.Versao);

        var anonimizada = await BodyAsync<ClienteBody>(await SendAsync(client, HttpMethod.Post, $"/clientes/{maria.Id:D}/anonimizar", t.Dono), HttpStatusCode.OK);
        Assert.Equal((maria.Id, "Cliente anonimizado", null, null, null, "anonimizado"), (anonimizada.Id, anonimizada.Nome, anonimizada.Telefone, anonimizada.Email, anonimizada.Documento, anonimizada.Estado));
        Assert.False(anonimizada.Consentimento.RevogadoEm is null);
        var row = Assert.Single(await QueryAsync(OrgA, UnitA, $"SELECT nome, telefone, email, documento, estado, anonimizado_em IS NOT NULL FROM clientes WHERE id = '{maria.Id:D}'", r => (r.GetString(0), r.IsDBNull(1), r.IsDBNull(2), r.IsDBNull(3), r.GetString(4), r.GetBoolean(5))));
        Assert.Equal(("Cliente anonimizado", true, true, true, "anonimizado", true), row);

        // The id, the link and the financial history stay.
        Assert.Equal(maria.Id, (await QueryAsync(OrgA, UnitA, $"SELECT cliente_id FROM comandas WHERE id = '{comanda:D}'", r => r.GetGuid(0))).Single());
        Assert.Equal((2200L, 1L), (await QueryAsync(OrgA, UnitA, $"SELECT aplicado_centavos, (SELECT count(*) FROM pagamentos WHERE comanda_id = '{comanda:D}') FROM pagamentos WHERE id = '{pago.Id:D}'", r => (r.GetInt64(0), r.GetInt64(1)))).Single());

        // It no longer shows in the search, cannot be changed or anonymized twice, and does not count as a duplicate of its old phone.
        Assert.Empty(await BodyAsync<List<ClienteBody>>(await SendAsync(client, HttpMethod.Get, "/clientes", t.Dono), HttpStatusCode.OK));
        Assert.Empty(await BodyAsync<List<ClienteBody>>(await SendAsync(client, HttpMethod.Get, "/clientes?busca=98765", t.Dono), HttpStatusCode.OK));
        Assert.Equal("anonimizado", (await BodyAsync<ClienteBody>(await SendAsync(client, HttpMethod.Get, $"/clientes/{maria.Id:D}", t.Dono), HttpStatusCode.OK)).Estado);
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, $"/clientes/{maria.Id:D}/anonimizar", t.Dono), HttpStatusCode.Conflict, "anonimizado");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Put, $"/clientes/{maria.Id:D}", t.Dono, new { nome = "Volta" }), HttpStatusCode.Conflict, "anonimizado");
        await AssertCustomerErrorAsync(await SendAsync(client, HttpMethod.Post, $"/clientes/{maria.Id:D}/revogar-consentimento", t.Dono), HttpStatusCode.Conflict, "anonimizado");
        _ = await RegisterCustomerAsync(client, t.Dono, NovoCliente("Novo dono do telefone", "11987654321"));

        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_id, depois::text FROM auditoria_registros WHERE acao = 'cliente:anonimizar'", r => (r.GetGuid(0), r.GetString(1), r.GetString(2))));
        Assert.Equal((DonoA, maria.Id.ToString("D")), (audit.Item1, audit.Item2));
        Assert.Contains("camposApagados", audit.Item3, StringComparison.Ordinal);
        Assert.DoesNotContain("Maria", audit.Item3, StringComparison.Ordinal);
        Assert.DoesNotContain("98765", audit.Item3, StringComparison.Ordinal);
        Assert.DoesNotContain("exemplo", audit.Item3, StringComparison.Ordinal);

        // The application credential cannot delete a customer: forgetting is anonymizing.
        await Assert.ThrowsAsync<PostgresException>(() => QueryAsync(OrgA, UnitA, "DELETE FROM clientes RETURNING 1", r => r.GetInt32(0)));
    }

    [Fact]
    public async Task The_customer_model_matches_the_database_and_the_queries_never_filter_by_organization()
    {
        await using var source = NpgsqlDataSource.Create(_appConnection);
        await using var tx = await new TenantDatabase(source).BeginAsync(new OrganizationContext(OrgA, UnitA));
        await using var context = new ClientesDbContext(tx);

        var busca = context.Clientes.AsNoTracking().Where(c => c.Estado == "ativo" && c.Telefone == "11987654321").OrderBy(c => c.Nome).ToQueryString();

        Assert.Contains("FROM clientes AS", busca, StringComparison.Ordinal);
        Assert.DoesNotContain("organizacao_id =", busca, StringComparison.Ordinal);
        Assert.Empty(await SchemaDrift.FindAsync(context));
    }
}
