using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Flow4Tech.Kernel;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Permissoes.Application;
using Flow4Tech.Permissoes.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Flow4Tech.Tests;

/// <summary>
/// F4-0069: the menu import through the real authenticated routes (real PostgreSQL, application credential with RLS, real sessions). The owner holds every permission; "Garcom" and "Mudo" hold
/// <c>cardapio:importar</c> only when a test gives it. Covers the preview (writes nothing, per-row errors), the apply (all or nothing, idempotent by import id, one audit record with counts only, one
/// event), CSV and XLSX, the limits (macro, formula, size), the permission matrix, the price limit of <c>cardapio:preco-alterar</c> and another organization.
/// </summary>
public sealed partial class ComandasEndpointsTests
{
    private const string Planilha =
        "tipo;codigo;nome;preco;categoria;grupo;minimo;maximo\r\n"
        + "categoria;;Bebidas;;;;;\r\n"
        + "categoria;;Cervejas;;Bebidas;;;\r\n"
        + "grupo;;Tamanho;;;;1;1\r\n"
        + "opcao;;Pequeno;0;;Tamanho;;\r\n"
        + "opcao;;Grande;3,50;;Tamanho;;\r\n"
        + "produto;CH01;Chopp;9,50;Cervejas;Tamanho;;\r\n"
        + "produto;SU01;Suco de laranja;8;Bebidas;;;\r\n";

    private sealed record ErroDeLinhaBody(int Linha, string Coluna, string Mensagem);

    private sealed record ItemDaPreviaBody(string Tipo, string Chave, string Acao, int Linha);

    private sealed record ResumoBody(int CategoriasCriadas, int CategoriasAtualizadas, int ProdutosCriados, int ProdutosAtualizados, int GruposCriados, int GruposAtualizados, int OpcoesCriadas, int OpcoesAtualizadas);

    private sealed record PreviaDaImportacaoBody(bool Valida, List<ErroDeLinhaBody> Erros, List<ItemDaPreviaBody> Itens, ResumoBody Resumo);

    private sealed record ImportacaoAplicadaBody(Guid ImportacaoId, bool JaAplicada, ResumoBody Resumo);

    private sealed record ModeloBody(List<string> Colunas, List<string> Tipos, List<string> Formatos, string ExemploCsv, int LinhasMaximas, int BytesMaximos);

    private static string Base64(string csv) => Convert.ToBase64String(Encoding.UTF8.GetBytes(csv));

    private static Task<HttpResponseMessage> PreviewImportAsync(HttpClient client, string? token, string base64, string formato = "csv") =>
        SendAsync(client, HttpMethod.Post, "/cardapio/importacao/previa", token, new { formato, conteudoBase64 = base64 });

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, string? token, Guid id, string base64, string formato = "csv") =>
        SendAsync(client, HttpMethod.Post, "/cardapio/importacao", token, new { importacaoId = id, formato, conteudoBase64 = base64 });

    /// <summary>A minimal workbook: shared strings for text, plain numbers for numbers, a formula cell and/or a VBA project when asked.</summary>
    private static string Xlsx(object[][] linhas, bool comFormula = false, bool comMacro = false)
    {
        var compartilhadas = new List<string>();
        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < linhas.Length; r++)
        {
            sheet.Append(System.Globalization.CultureInfo.InvariantCulture, $"<row r=\"{r + 1}\">");
            for (var c = 0; c < linhas[r].Length; c++)
            {
                var referencia = $"{(char)('A' + c)}{r + 1}";
                switch (linhas[r][c])
                {
                    case string s when s.Length == 0:
                        break;
                    case string s:
                        compartilhadas.Add(s);
                        sheet.Append(System.Globalization.CultureInfo.InvariantCulture, $"<c r=\"{referencia}\" t=\"s\"><v>{compartilhadas.Count - 1}</v></c>");
                        break;
                    case double d:
                        sheet.Append(System.Globalization.CultureInfo.InvariantCulture, $"<c r=\"{referencia}\"><v>{d}</v></c>");
                        break;
                }
            }

            if (comFormula && r == 1)
            {
                sheet.Append(System.Globalization.CultureInfo.InvariantCulture, $"<c r=\"J{r + 1}\"><f>1+1</f><v>2</v></c>");
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");
        using var memoria = new MemoryStream();
        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, true))
        {
            void Escrever(string nome, string conteudo)
            {
                using var w = new StreamWriter(zip.CreateEntry(nome).Open(), new UTF8Encoding(false));
                w.Write(conteudo);
            }

            Escrever("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>");
            Escrever("xl/workbook.xml", "<?xml version=\"1.0\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Cardapio\" sheetId=\"1\" r:id=\"rId7\"/></sheets></workbook>");
            Escrever("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId7\" Type=\"worksheet\" Target=\"worksheets/planilha-do-cardapio.xml\"/></Relationships>");
            Escrever("xl/worksheets/planilha-do-cardapio.xml", sheet.ToString());
            Escrever("xl/sharedStrings.xml", "<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" + string.Concat(compartilhadas.Select(s => $"<si><t>{System.Security.SecurityElement.Escape(s)}</t></si>")) + "</sst>");
            if (comMacro)
            {
                Escrever("xl/vbaProject.bin", "macros");
            }
        }

        return Convert.ToBase64String(memoria.ToArray());
    }

    private static object[][] PlanilhaEmXlsx() =>
    [
        ["tipo", "codigo", "nome", "preco", "categoria", "grupo", "minimo", "maximo"],
        ["categoria", "", "Bebidas", "", "", "", "", ""],
        ["produto", "ch01", "Chopp", 9.1d, "Bebidas", "", "", ""],
        ["produto", "su01", "Suco", 12.0999999999999996d, "Bebidas", "", "", ""],
    ];

    private async Task<string> GiveImporterAsync(Api api, Guid account, string profileName, params RegraDeAcesso[] regras)
    {
        var perfis = api.Services.GetRequiredService<GestaoDePerfis>();
        var request = new FakeRequest(DonoA, new OrganizationContext(OrgA, UnitA));
        var perfil = await perfis.CriarAsync(request, profileName, [new RegraDeAcesso(RuleEffect.Allow, "cardapio:importar"), .. regras]);
        Assert.Equal(ResultadoGestao.Ok, perfil.Resultado);
        Assert.Equal(ResultadoGestao.Ok, await perfis.AtribuirAsync(request, account, perfil.Id!.Value));
        return await FreshTokenAsync(api, account);
    }

    private async Task<long> PriceOfAsync(string codigo) => (await QueryAsync(OrgA, UnitA, $"SELECT preco_centavos FROM produtos WHERE codigo = '{codigo}'", r => r.GetInt64(0))).Single();

    [Fact]
    public async Task The_template_is_described_to_the_owner_only()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var modelo = await BodyAsync<ModeloBody>(await SendAsync(client, HttpMethod.Get, "/cardapio/importacao/modelo", t.Dono), HttpStatusCode.OK);
        using var garcom = await SendAsync(client, HttpMethod.Get, "/cardapio/importacao/modelo", t.Garcom);
        using var anonimo = await SendAsync(client, HttpMethod.Get, "/cardapio/importacao/modelo", null);

        Assert.Equal(["tipo", "codigo", "nome", "preco", "categoria", "grupo", "minimo", "maximo"], modelo.Colunas);
        Assert.Equal(["csv", "xlsx"], modelo.Formatos);
        Assert.Equal((2000, 1_048_576), (modelo.LinhasMaximas, modelo.BytesMaximos));
        Assert.Equal(HttpStatusCode.Forbidden, garcom.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonimo.StatusCode);

        // The example of the template is itself a sheet the preview accepts.
        var previa = await BodyAsync<PreviaDaImportacaoBody>(await PreviewImportAsync(client, t.Dono, Base64(modelo.ExemploCsv)), HttpStatusCode.OK);
        Assert.True(previa.Valida);
    }

    [Fact]
    public async Task A_preview_says_what_would_happen_and_writes_nothing_anywhere()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var previa = await BodyAsync<PreviaDaImportacaoBody>(await PreviewImportAsync(client, t.Dono, Base64(Planilha)), HttpStatusCode.OK);
        var denovo = await BodyAsync<PreviaDaImportacaoBody>(await PreviewImportAsync(client, t.Dono, Base64(Planilha)), HttpStatusCode.OK);

        Assert.True(previa.Valida);
        Assert.Empty(previa.Erros);
        Assert.Equal(new ResumoBody(2, 0, 2, 0, 1, 0, 2, 0), previa.Resumo);
        Assert.Equal(7, previa.Itens.Count);
        Assert.All(previa.Itens, i => Assert.Equal("criar", i.Acao));
        Assert.Equal(("produto", "CH01", 7), (previa.Itens[5].Tipo, previa.Itens[5].Chave, previa.Itens[5].Linha + 1));
        Assert.Equal(previa.Resumo, denovo.Resumo);
        Assert.Equal(0L, await CountAsync("produtos"));
        Assert.Equal(0L, await CountAsync("categorias"));
        Assert.Equal(0L, await CountAsync("grupos_opcao"));
        Assert.Equal(0L, await CountAsync("importacoes_cardapio"));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'cardapio:importar'"));
        Assert.Equal(0L, await CountAsync("outbox_eventos WHERE tipo = 'cardapio.importado'"));
    }

    [Fact]
    public async Task A_preview_reports_every_wrong_row_with_line_column_and_message()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var csv = "tipo;codigo;nome;preco;categoria;grupo;minimo;maximo\r\n"
            + "categoria;;Bebidas;;;;;\r\n"
            + "produto;CH01;Chopp;abc;Bebidas;;;\r\n"
            + "produto;SU01;Suco;8;Fantasma;;;\r\n"
            + "\r\n"
            + "produto;CH01;Outro;9;Bebidas;;;\r\n";

        var previa = await BodyAsync<PreviaDaImportacaoBody>(await PreviewImportAsync(client, t.Dono, Base64(csv)), HttpStatusCode.OK);

        Assert.False(previa.Valida);
        Assert.Equal(
            [
                new ErroDeLinhaBody(3, "preco", "Preço inválido. Use reais com até duas casas, por exemplo 25,90."),
                new ErroDeLinhaBody(4, "categoria", "A categoria 'Fantasma' não existe no cardápio nem na planilha."),
            ],
            previa.Erros);
        Assert.Empty(previa.Itens);
    }

    [Fact]
    public async Task Applying_creates_everything_in_one_unit_with_an_audit_record_of_counts_only_and_one_event()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var id = Guid.CreateVersion7();

        var r = await BodyAsync<ImportacaoAplicadaBody>(await ImportAsync(client, t.Dono, id, Base64(Planilha)), HttpStatusCode.Created);

        Assert.Equal((id, false, new ResumoBody(2, 0, 2, 0, 1, 0, 2, 0)), (r.ImportacaoId, r.JaAplicada, r.Resumo));
        Assert.Equal([("Bebidas", (Guid?)null), ("Cervejas", (Guid?)(await QueryAsync(OrgA, UnitA, "SELECT id FROM categorias WHERE nome = 'Bebidas'", x => x.GetGuid(0))).Single())], await QueryAsync(OrgA, UnitA, "SELECT nome, categoria_pai_id FROM categorias ORDER BY nome", x => (x.GetString(0), x.IsDBNull(1) ? (Guid?)null : x.GetGuid(1))));
        Assert.Equal([("CH01", "Chopp", 950L, "BRL", true, 1, "Cervejas"), ("SU01", "Suco de laranja", 800L, "BRL", true, 1, "Bebidas")], await QueryAsync(OrgA, UnitA, "SELECT p.codigo, p.nome, p.preco_centavos, p.moeda, p.ativo, p.versao, c.nome FROM produtos p JOIN categorias c ON c.id = p.categoria_id ORDER BY p.codigo", x => (x.GetString(0), x.GetString(1), x.GetInt64(2), x.GetString(3), x.GetBoolean(4), x.GetInt32(5), x.GetString(6))));
        Assert.Equal([("Grande", 350L, 2), ("Pequeno", 0L, 1)], await QueryAsync(OrgA, UnitA, "SELECT o.nome, o.preco_centavos, o.posicao FROM opcoes o JOIN grupos_opcao g ON g.id = o.grupo_id WHERE g.nome = 'Tamanho' ORDER BY o.nome", x => (x.GetString(0), x.GetInt64(1), x.GetInt32(2))));
        Assert.Equal([("Tamanho", 1, 1)], await QueryAsync(OrgA, UnitA, "SELECT g.nome, g.minimo, g.maximo FROM produto_grupos_opcao v JOIN grupos_opcao g ON g.id = v.grupo_id JOIN produtos p ON p.id = v.produto_id WHERE p.codigo = 'CH01'", x => (x.GetString(0), x.GetInt32(1), x.GetInt32(2))));
        Assert.Equal(0L, await CountAsync("produto_grupos_opcao v JOIN produtos p ON p.id = v.produto_id WHERE p.codigo = 'SU01'"));

        var audit = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT usuario_id, objeto_tipo, objeto_id, resultado, depois::text FROM auditoria_registros WHERE acao = 'cardapio:importar'", x => (x.GetGuid(0), x.GetString(1), x.GetString(2), x.GetString(3), x.GetString(4))));
        Assert.Equal((DonoA, "cardapio", id.ToString("D"), "sucesso"), (audit.Item1, audit.Item2, audit.Item3, audit.Item4));
        Assert.Contains("\"produtosCriados\": 2", audit.Item5, StringComparison.Ordinal);
        Assert.DoesNotContain("Chopp", audit.Item5, StringComparison.Ordinal);
        Assert.DoesNotContain("CH01", audit.Item5, StringComparison.Ordinal);
        var evento = Assert.Single(await QueryAsync(OrgA, UnitA, "SELECT payload::text FROM outbox_eventos WHERE tipo = 'cardapio.importado'", x => x.GetString(0)));
        Assert.Contains(id.ToString("D"), evento, StringComparison.Ordinal);
        Assert.Equal(1L, await CountAsync("importacoes_cardapio"));
        Assert.Equal((2, 2, 1, 2), (await QueryAsync(OrgA, UnitA, "SELECT categorias_criadas, produtos_criados, grupos_criados, opcoes_criadas FROM importacoes_cardapio", x => (x.GetInt32(0), x.GetInt32(1), x.GetInt32(2), x.GetInt32(3)))).Single());
    }

    [Fact]
    public async Task Repeating_an_import_id_answers_the_first_result_without_writing_and_another_file_with_the_same_id_is_refused()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var id = Guid.CreateVersion7();
        var primeira = await BodyAsync<ImportacaoAplicadaBody>(await ImportAsync(client, t.Dono, id, Base64(Planilha)), HttpStatusCode.Created);

        var repetida = await BodyAsync<ImportacaoAplicadaBody>(await ImportAsync(client, t.Dono, id, Base64(Planilha)), HttpStatusCode.OK);
        using var outroArquivo = await ImportAsync(client, t.Dono, id, Base64(Planilha.Replace("9,50", "19,50", StringComparison.Ordinal)));

        Assert.True(repetida.JaAplicada);
        Assert.Equal(primeira.Resumo, repetida.Resumo);
        await AssertErrorAsync(outroArquivo, HttpStatusCode.Conflict, "importacao-id-reutilizado");
        Assert.Equal(950L, await PriceOfAsync("CH01"));
        Assert.Equal((1L, 1L, 1L), (await CountAsync("importacoes_cardapio"), await CountAsync("auditoria_registros WHERE acao = 'cardapio:importar'"), await CountAsync("outbox_eventos WHERE tipo = 'cardapio.importado'")));
    }

    [Fact]
    public async Task Two_applies_of_the_same_id_at_once_write_one_set_of_rows()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var id = Guid.CreateVersion7();

        var respostas = await Task.WhenAll(ImportAsync(client, t.Dono, id, Base64(Planilha)), ImportAsync(client, t.Dono, id, Base64(Planilha)));

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Created], respostas.Select(r => r.StatusCode).Order());
        Assert.Equal((2L, 2L, 1L), (await CountAsync("categorias"), await CountAsync("produtos"), await CountAsync("importacoes_cardapio")));
        Assert.Equal(1L, await CountAsync("outbox_eventos WHERE tipo = 'cardapio.importado'"));
        foreach (var r in respostas)
        {
            r.Dispose();
        }
    }

    [Fact]
    public async Task A_second_import_of_the_same_sheet_matches_by_short_code_so_nothing_is_duplicated_and_a_changed_price_updates_in_place()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        using var primeira = await ImportAsync(client, t.Dono, Guid.CreateVersion7(), Base64(Planilha));
        Assert.Equal(HttpStatusCode.Created, primeira.StatusCode);
        var chopp = (await QueryAsync(OrgA, UnitA, "SELECT id FROM produtos WHERE codigo = 'CH01'", x => x.GetGuid(0))).Single();

        var igual = await BodyAsync<ImportacaoAplicadaBody>(await ImportAsync(client, t.Dono, Guid.CreateVersion7(), Base64(Planilha)), HttpStatusCode.Created);
        var maisCaro = await BodyAsync<ImportacaoAplicadaBody>(await ImportAsync(client, t.Dono, Guid.CreateVersion7(), Base64(Planilha.Replace("9,50", "10,00", StringComparison.Ordinal).Replace("Pequeno;0", "Pequeno;0,50", StringComparison.Ordinal))), HttpStatusCode.Created);

        Assert.Equal(new ResumoBody(0, 0, 0, 0, 0, 0, 0, 0), igual.Resumo);
        Assert.Equal(new ResumoBody(0, 0, 0, 1, 0, 1, 0, 1), maisCaro.Resumo);
        Assert.Equal((2L, 2L, 1L, 2L), (await CountAsync("categorias"), await CountAsync("produtos"), await CountAsync("grupos_opcao"), await CountAsync("opcoes")));
        Assert.Equal((chopp, 1000L, 2), (await QueryAsync(OrgA, UnitA, "SELECT id, preco_centavos, versao FROM produtos WHERE codigo = 'CH01'", x => (x.GetGuid(0), x.GetInt64(1), x.GetInt32(2)))).Single());
        Assert.Equal(50L, (await QueryAsync(OrgA, UnitA, "SELECT preco_centavos FROM opcoes WHERE nome = 'Pequeno'", x => x.GetInt64(0))).Single());
        Assert.Equal(3L, await CountAsync("importacoes_cardapio"));
        Assert.Equal(3L, await CountAsync("auditoria_registros WHERE acao = 'cardapio:importar'"));
    }

    [Fact]
    public async Task One_wrong_row_among_valid_ones_refuses_the_whole_sheet_and_writes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var id = Guid.CreateVersion7();

        var relatorio = await BodyAsync<PreviaDaImportacaoBody>(await ImportAsync(client, t.Dono, id, Base64(Planilha + "produto;XX1;Sem preco;;Bebidas;;;\r\n")), HttpStatusCode.UnprocessableEntity);

        Assert.False(relatorio.Valida);
        Assert.Equal([new ErroDeLinhaBody(8, "preco", "O preço é obrigatório.")], relatorio.Erros);
        Assert.Equal((0L, 0L, 0L, 0L), (await CountAsync("categorias"), await CountAsync("produtos"), await CountAsync("grupos_opcao"), await CountAsync("importacoes_cardapio")));
        Assert.Equal(0L, await CountAsync("auditoria_registros WHERE acao = 'cardapio:importar'"));

        // Fixing the sheet and sending it again with the same id applies it: a refused attempt does not burn the id.
        using var corrigida = await ImportAsync(client, t.Dono, id, Base64(Planilha));
        Assert.Equal(HttpStatusCode.Created, corrigida.StatusCode);
    }

    [Fact]
    public async Task An_xlsx_sheet_is_read_by_value_with_numbers_as_exact_cents_and_the_template_columns_in_any_order()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        var r = await BodyAsync<ImportacaoAplicadaBody>(await ImportAsync(client, t.Dono, Guid.CreateVersion7(), Xlsx(PlanilhaEmXlsx()), "xlsx"), HttpStatusCode.Created);

        Assert.Equal(new ResumoBody(1, 0, 2, 0, 0, 0, 0, 0), r.Resumo);
        Assert.Equal(910L, await PriceOfAsync("CH01"));
        Assert.Equal(1210L, await PriceOfAsync("SU01"));
    }

    [Fact]
    public async Task A_workbook_with_a_formula_or_a_macro_and_files_that_are_too_big_or_not_a_sheet_are_refused_before_any_row_is_read()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        using var formula = await ImportAsync(client, t.Dono, Guid.CreateVersion7(), Xlsx(PlanilhaEmXlsx(), comFormula: true), "xlsx");
        using var macro = await PreviewImportAsync(client, t.Dono, Xlsx(PlanilhaEmXlsx(), comMacro: true), "xlsx");
        using var csvComFormula = await PreviewImportAsync(client, t.Dono, Base64(Planilha.Replace("9,50", "=1+1", StringComparison.Ordinal)));
        using var grande = await PreviewImportAsync(client, t.Dono, Base64(new string('a', 1_048_577)));
        using var linhas = await PreviewImportAsync(client, t.Dono, Base64("tipo;nome\r\n" + string.Concat(Enumerable.Repeat("categoria;A\r\n", 2001))));
        using var naoEPlanilha = await PreviewImportAsync(client, t.Dono, Convert.ToBase64String("isto nao e um zip"u8.ToArray()), "xlsx");
        using var formatoRuim = await PreviewImportAsync(client, t.Dono, Base64(Planilha), "pdf");
        using var base64Ruim = await PreviewImportAsync(client, t.Dono, "@@@@");

        var f = await formula.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal((HttpStatusCode.BadRequest, "arquivo-com-formula", 2), (formula.StatusCode, f.GetProperty("codigo").GetString(), f.GetProperty("linha").GetInt32()));
        await AssertErrorAsync(macro, HttpStatusCode.BadRequest, "arquivo-com-macro");
        await AssertErrorAsync(csvComFormula, HttpStatusCode.BadRequest, "arquivo-com-formula");
        await AssertErrorAsync(grande, HttpStatusCode.BadRequest, "arquivo-grande");
        await AssertErrorAsync(linhas, HttpStatusCode.BadRequest, "linhas-demais");
        await AssertErrorAsync(naoEPlanilha, HttpStatusCode.BadRequest, "arquivo-invalido");
        await AssertErrorAsync(formatoRuim, HttpStatusCode.BadRequest, "formato-invalido");
        await AssertErrorAsync(base64Ruim, HttpStatusCode.BadRequest, "formato-invalido");
        Assert.Equal((0L, 0L), (await CountAsync("produtos"), await CountAsync("importacoes_cardapio")));
    }

    [Fact]
    public async Task A_semicolon_or_comma_csv_in_utf8_or_windows_1252_with_quoted_cells_is_read()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var virgulas = "tipo,codigo,nome,preco,categoria\r\ncategoria,,\"Pratos, e petiscos\",,\r\nproduto,PF01,\"Prato \"\"feito\"\"\",\"25,90\",\"Pratos, e petiscos\"\r\n";

        var utf8 = await BodyAsync<PreviaDaImportacaoBody>(await PreviewImportAsync(client, t.Dono, Base64("﻿" + virgulas)), HttpStatusCode.OK);
        var latin1 = await BodyAsync<PreviaDaImportacaoBody>(await PreviewImportAsync(client, t.Dono, Convert.ToBase64String(Encoding.Latin1.GetBytes("tipo;nome;categoria;codigo;preco\r\ncategoria;Ação;;;\r\nproduto;Pão;Ação;PA01;5\r\n"))), HttpStatusCode.OK);

        Assert.Empty(utf8.Erros);
        Assert.Equal(new ResumoBody(1, 0, 1, 0, 0, 0, 0, 0), utf8.Resumo);
        Assert.Equal(["Ação", "PA01"], new[] { latin1.Itens[0].Chave, latin1.Itens[1].Chave });
    }

    [Fact]
    public async Task Importing_needs_the_import_permission_and_a_forbidden_caller_changes_nothing()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);

        foreach (var token in new[] { t.Garcom, t.Mudo })
        {
            using var previa = await PreviewImportAsync(client, token, Base64(Planilha));
            using var aplicar = await ImportAsync(client, token, Guid.CreateVersion7(), Base64(Planilha));
            Assert.All(new[] { previa, aplicar }, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
            Assert.Empty(await ReadBodyAsync(aplicar));
        }

        using var anonimo = await ImportAsync(client, null, Guid.CreateVersion7(), Base64(Planilha));
        Assert.Equal(HttpStatusCode.Unauthorized, anonimo.StatusCode);
        Assert.Equal((0L, 0L, 0L), (await CountAsync("produtos"), await CountAsync("importacoes_cardapio"), await CountAsync("auditoria_registros WHERE acao = 'cardapio:importar'")));
    }

    [Fact]
    public async Task Another_organization_imports_on_its_own_and_neither_sees_nor_changes_the_rows_or_the_ids_of_the_first()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        var id = Guid.CreateVersion7();
        using var deA = await ImportAsync(client, t.Dono, id, Base64(Planilha));
        Assert.Equal(HttpStatusCode.Created, deA.StatusCode);

        // The same codes, names and even the same import id are new in the other organization (ids are scoped by organization).
        var deB = await BodyAsync<ImportacaoAplicadaBody>(await ImportAsync(client, t.DonoB, id, Base64(Planilha.Replace("9,50", "11", StringComparison.Ordinal))), HttpStatusCode.Created);

        Assert.False(deB.JaAplicada);
        Assert.Equal(950L, await PriceOfAsync("CH01"));
        Assert.Equal((2L, 2L, 1L), (await CountAsync("categorias"), await CountAsync("produtos"), await CountAsync("importacoes_cardapio")));
        Assert.Equal((1100L, 1L), (await QueryAsync(OrgB, UnitB, "SELECT preco_centavos, (SELECT count(*) FROM importacoes_cardapio) FROM produtos WHERE codigo = 'CH01'", x => (x.GetInt64(0), x.GetInt64(1)))).Single());
    }

    [Fact]
    public async Task A_price_change_of_an_existing_product_still_answers_to_the_price_limit_and_a_refusal_rolls_the_whole_import_back()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        using var semente = await ImportAsync(client, t.Dono, Guid.CreateVersion7(), Base64(Planilha));
        Assert.Equal(HttpStatusCode.Created, semente.StatusCode);
        var comLimite = await GiveImporterAsync(api, Mudo, "Importa com limite", new RegraDeAcesso(RuleEffect.Allow, "cardapio:preco-alterar", MaxPercent: 10m));
        var semPermissao = await GiveImporterAsync(api, Garcom, "So importa");

        // 100% up with a new category in the same sheet: refused (403, no reason), and the new category does not stay either.
        var dobra = Planilha.Replace("9,50", "19,00", StringComparison.Ordinal) + "categoria;;Doces;;;;;\r\n";
        using var recusada = await ImportAsync(client, comLimite, Guid.CreateVersion7(), Base64(dobra));
        using var semPreco = await ImportAsync(client, semPermissao, Guid.CreateVersion7(), Base64(Planilha.Replace("9,50", "9,60", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.Forbidden, recusada.StatusCode);
        Assert.Empty(await ReadBodyAsync(recusada));
        Assert.Equal(HttpStatusCode.Forbidden, semPreco.StatusCode);
        Assert.Equal(950L, await PriceOfAsync("CH01"));
        Assert.Equal((2L, 1L), (await CountAsync("categorias"), await CountAsync("importacoes_cardapio")));

        // Inside the limit it applies; and creating new rows needs no price permission at all.
        using var dentro = await ImportAsync(client, comLimite, Guid.CreateVersion7(), Base64(Planilha.Replace("9,50", "9,90", StringComparison.Ordinal)));
        using var soCria = await ImportAsync(client, semPermissao, Guid.CreateVersion7(), Base64(Planilha + "categoria;;Doces;;;;;\r\n"));
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (dentro.StatusCode, soCria.StatusCode));
        Assert.Equal(990L, await PriceOfAsync("CH01"));
        Assert.Equal(3L, await CountAsync("categorias"));
    }

    [Fact]
    public async Task The_record_of_imports_cannot_be_changed_or_deleted_with_the_application_credential()
    {
        await using var api = new Api(_appConnection);
        using var client = api.CreateClient();
        var t = await SeedAsync(api);
        using var aplicada = await ImportAsync(client, t.Dono, Guid.CreateVersion7(), Base64(Planilha));
        Assert.Equal(HttpStatusCode.Created, aplicada.StatusCode);

        await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => RunSqlAsync(OrgA, UnitA, "UPDATE importacoes_cardapio SET produtos_criados = 99"));
        await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => RunSqlAsync(OrgA, UnitA, "DELETE FROM importacoes_cardapio"));
        Assert.Equal(2, (await QueryAsync(OrgA, UnitA, "SELECT produtos_criados FROM importacoes_cardapio", x => x.GetInt32(0))).Single());
    }
}
