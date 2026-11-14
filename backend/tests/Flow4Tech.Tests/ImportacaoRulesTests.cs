using Flow4Tech.Cardapio.Domain;

namespace Flow4Tech.Tests;

/// <summary>F4-0069: pure rules of the menu import (template header, per-row errors, creates versus updates matched by short code and name, prices in cents). No I/O.</summary>
public sealed class ImportacaoRulesTests
{
    private const string Cabecalho = "tipo;codigo;nome;preco;categoria;grupo;minimo;maximo";

    private static readonly EstadoDoCardapio Vazio = new([], [], [], new Dictionary<Guid, IReadOnlyList<Guid>>());

    private static List<LinhaDaPlanilha> Planilha(params string[] linhas) =>
        [.. linhas.Select((l, i) => new LinhaDaPlanilha(i + 1, l.Split(';')))];

    private static List<LinhaDaPlanilha> ComCabecalho(params string[] linhas) => Planilha([Cabecalho, .. linhas]);

    private static EstadoDoCardapio EstadoDe(PlanoDaImportacao plano) => new(
        [.. plano.Categorias.Select(c => c.Categoria)],
        [.. plano.Produtos.Select(p => p.Produto)],
        [.. plano.Grupos.Select(g => g.Grupo)],
        plano.Produtos.Where(p => p.GrupoIds is not null).ToDictionary(p => p.Produto.Id, p => p.GrupoIds!));

    [Theory]
    [InlineData("25,90", 2590)]
    [InlineData("25.9", 2590)]
    [InlineData("R$ 10", 1000)]
    [InlineData("  7,5 ", 750)]
    [InlineData("0", 0)]
    [InlineData("0,05", 5)]
    [InlineData("1000000,00", 100_000_000)]
    public void A_price_text_becomes_integer_cents(string texto, long centavos) => Assert.Equal(centavos, RegrasDaImportacao.Preco(texto));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("10,999")]
    [InlineData("1.234,56")]
    [InlineData("1000000,01")]
    [InlineData("12,")]
    [InlineData("99999999999999999999")]
    public void A_price_text_that_is_not_money_is_refused(string? texto) => Assert.Null(RegrasDaImportacao.Preco(texto));

    [Fact]
    public void A_complete_sheet_plans_every_create_with_counts_and_references_resolved_by_name()
    {
        var plano = RegrasDaImportacao.Planejar(
            ComCabecalho(
                "categoria;;Bebidas;;;;;",
                "categoria;;Cervejas;;Bebidas;;;",
                "produto;ch01;Chopp;9,50;Cervejas;Tamanho;;",
                "produto;x1;Hamburguer;25,90;bebidas;Adicionais|Tamanho;;",
                "grupo;;Adicionais;;;;0;2",
                "grupo;;Tamanho;;;;1;1",
                "opcao;;Bacon;5;;Adicionais;;",
                "opcao;;Grande;3,00;;Tamanho;;"),
            Vazio);

        Assert.Empty(plano.Erros);
        Assert.True(plano.Valido);
        Assert.Equal(new ResumoDaImportacao(2, 0, 2, 0, 2, 0, 2, 0), plano.Resumo);
        Assert.All(plano.Itens, i => Assert.Equal(AcaoDaImportacao.Criar, i.Acao));
        Assert.Equal(8, plano.Itens.Count);
        var bebidas = plano.Categorias.Single(c => c.Categoria.Nome == "Bebidas").Categoria;
        var cervejas = plano.Categorias.Single(c => c.Categoria.Nome == "Cervejas").Categoria;
        Assert.Equal(bebidas.Id, cervejas.CategoriaPaiId);
        var chopp = plano.Produtos.Single(p => p.Produto.Codigo == "CH01");
        Assert.Equal((cervejas.Id, 950L, "BRL", true, 1), (chopp.Produto.CategoriaId, chopp.Produto.PrecoCentavos, chopp.Produto.Moeda, chopp.Produto.Ativo, chopp.Produto.Versao));
        var tamanho = plano.Grupos.Single(g => g.Grupo.Nome == "Tamanho").Grupo;
        var adicionais = plano.Grupos.Single(g => g.Grupo.Nome == "Adicionais").Grupo;
        Assert.Equal([tamanho.Id], chopp.GrupoIds);
        Assert.Equal([adicionais.Id, tamanho.Id], plano.Produtos.Single(p => p.Produto.Codigo == "X1").GrupoIds);
        Assert.Equal(("Adicionais", 0, 2, true, 1), (adicionais.Nome, adicionais.Minimo, adicionais.Maximo, adicionais.Ativo, adicionais.Versao));
        Assert.Equal([("Bacon", 500L, true)], adicionais.Opcoes.Select(o => (o.Nome, o.PrecoCentavos, o.Ativa)));
        Assert.Equal([("produto", "CH01", 4), ("grupo", "Adicionais", 6), ("opcao", "Tamanho / Grande", 9)], [.. plano.Itens.Where(i => i.Linha is 4 or 6 or 9).Select(i => (i.Tipo, i.Chave, i.Linha))]);
    }

    [Fact]
    public void Planning_again_what_was_just_planned_changes_nothing_so_a_second_import_never_duplicates()
    {
        var linhas = ComCabecalho(
            "categoria;;Bebidas;;;;;",
            "produto;ch01;Chopp;9,50;Bebidas;Tamanho;;",
            "grupo;;Tamanho;;;;1;1",
            "opcao;;Grande;3,00;;Tamanho;;");
        var primeiro = RegrasDaImportacao.Planejar(linhas, Vazio);

        var segundo = RegrasDaImportacao.Planejar(linhas, EstadoDe(primeiro));

        Assert.Empty(segundo.Erros);
        Assert.All(segundo.Itens, i => Assert.Equal(AcaoDaImportacao.SemMudanca, i.Acao));
        Assert.Equal(new ResumoDaImportacao(0, 0, 0, 0, 0, 0, 0, 0), segundo.Resumo);
        Assert.Equal(primeiro.Produtos[0].Produto.Id, segundo.Produtos[0].Produto.Id);
        Assert.Equal(primeiro.Categorias[0].Categoria.Id, segundo.Categorias[0].Categoria.Id);
    }

    [Fact]
    public void A_row_matching_an_existing_product_by_code_updates_it_keeping_what_the_sheet_does_not_say()
    {
        var cat = new Categoria(Guid.CreateVersion7(), "Lanches", true, 3);
        var outra = new Categoria(Guid.CreateVersion7(), "Bebidas", true, 1);
        var promo = new Promocao(1500, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1));
        var existente = new Produto(Guid.CreateVersion7(), cat.Id, "HAMB", "Hamburguer", 2000, "BRL", false, 4, true, promo);
        var estado = new EstadoDoCardapio([cat, outra], [existente], [], new Dictionary<Guid, IReadOnlyList<Guid>>());

        var plano = RegrasDaImportacao.Planejar(ComCabecalho("produto;hamb;Hamburguer Artesanal;25,90;Bebidas;;;"), estado);

        Assert.Empty(plano.Erros);
        var p = Assert.Single(plano.Produtos);
        Assert.Equal(AcaoDaImportacao.Atualizar, p.Acao);
        Assert.Equal(existente with { Nome = "Hamburguer Artesanal", PrecoCentavos = 2590, CategoriaId = outra.Id, Versao = 5 }, p.Produto);
        Assert.Null(p.GrupoIds);
        Assert.Equal(new ResumoDaImportacao(0, 0, 0, 1, 0, 0, 0, 0), plano.Resumo);
    }

    [Fact]
    public void A_blank_category_or_groups_cell_keeps_what_an_existing_product_has_but_a_new_product_needs_a_category()
    {
        var cat = new Categoria(Guid.CreateVersion7(), "Lanches", true, 1);
        var existente = new Produto(Guid.CreateVersion7(), cat.Id, "HAMB", "Hamburguer", 2000, "BRL", true, 1);
        var estado = new EstadoDoCardapio([cat], [existente], [], new Dictionary<Guid, IReadOnlyList<Guid>>());

        var mantido = RegrasDaImportacao.Planejar(ComCabecalho("produto;HAMB;Hamburguer;20;;;;"), estado);
        var novo = RegrasDaImportacao.Planejar(ComCabecalho("produto;NOVO;Suco;8;;;;"), estado);

        Assert.Empty(mantido.Erros);
        Assert.Equal(AcaoDaImportacao.SemMudanca, Assert.Single(mantido.Produtos).Acao);
        Assert.Equal([new ErroDeLinha(2, "categoria", "A categoria é obrigatória para um produto novo.")], novo.Erros);
    }

    [Fact]
    public void Existing_groups_get_new_options_and_updated_prices_without_turning_off_the_ones_the_sheet_omits()
    {
        var bacon = new Opcao(Guid.CreateVersion7(), "Bacon", 500, true);
        var queijo = new Opcao(Guid.CreateVersion7(), "Queijo", 300, false);
        var grupo = new GrupoDeOpcao(Guid.CreateVersion7(), "Adicionais", 0, 2, true, 7, [bacon, queijo]);
        var estado = new EstadoDoCardapio([], [], [grupo], new Dictionary<Guid, IReadOnlyList<Guid>>());

        var plano = RegrasDaImportacao.Planejar(ComCabecalho("opcao;;BACON;6;;adicionais;;", "opcao;;Queijo;3;;Adicionais;;", "opcao;;Ovo;2;;Adicionais;;"), estado);

        Assert.Empty(plano.Erros);
        var g = Assert.Single(plano.Grupos);
        Assert.Equal(AcaoDaImportacao.Atualizar, g.Acao);
        Assert.Equal(8, g.Grupo.Versao);
        Assert.Equal([(bacon.Id, "Bacon", 600L, true), (queijo.Id, "Queijo", 300L, true)], g.Grupo.Opcoes.Take(2).Select(o => (o.Id, o.Nome, o.PrecoCentavos, o.Ativa)));
        Assert.Equal(("Ovo", 200L, true), (g.Grupo.Opcoes[2].Nome, g.Grupo.Opcoes[2].PrecoCentavos, g.Grupo.Opcoes[2].Ativa));
        Assert.Equal(new ResumoDaImportacao(0, 0, 0, 0, 0, 1, 1, 2), plano.Resumo);
    }

    [Fact]
    public void A_group_row_changes_the_limits_of_an_existing_group_and_a_same_limits_row_changes_nothing()
    {
        var grupo = new GrupoDeOpcao(Guid.CreateVersion7(), "Ponto", 1, 1, true, 2, [new Opcao(Guid.CreateVersion7(), "Mal", 0, true)]);
        var estado = new EstadoDoCardapio([], [], [grupo], new Dictionary<Guid, IReadOnlyList<Guid>>());

        var muda = RegrasDaImportacao.Planejar(ComCabecalho("grupo;;ponto;;;;0;2"), estado);
        var igual = RegrasDaImportacao.Planejar(ComCabecalho("grupo;;Ponto;;;;1;1"), estado);

        Assert.Equal((AcaoDaImportacao.Atualizar, 0, 2, 3), (muda.Grupos[0].Acao, muda.Grupos[0].Grupo.Minimo, muda.Grupos[0].Grupo.Maximo, muda.Grupos[0].Grupo.Versao));
        Assert.Equal(AcaoDaImportacao.SemMudanca, igual.Grupos[0].Acao);
        Assert.Equal(2, igual.Grupos[0].Grupo.Versao);
    }

    [Fact]
    public void The_header_is_matched_by_name_ignoring_case_accents_and_order_and_blank_lines_are_skipped()
    {
        var linhas = new List<LinhaDaPlanilha>
        {
            new(1, [""]),
            new(2, ["Nome", "TIPO", "Preço", "Código", "Categoria"]),
            new(3, ["", "", "", "", ""]),
            new(4, ["Bebidas", "Categoria", "", "", ""]),
            new(5, ["Suco", "Produto", "8,00", "su1", "Bebidas"]),
        };

        var plano = RegrasDaImportacao.Planejar(linhas, Vazio);

        Assert.Empty(plano.Erros);
        Assert.Equal(("SU1", 800L), (plano.Produtos[0].Produto.Codigo, plano.Produtos[0].Produto.PrecoCentavos));
        Assert.Equal(5, plano.Itens[1].Linha);
    }

    [Fact]
    public void Header_problems_are_reported_on_the_header_line_and_stop_the_reading()
    {
        var plano = RegrasDaImportacao.Planejar(Planilha("tipo;nome;nome;cor", "categoria;Bebidas;Bebidas;azul"), Vazio);

        Assert.Equal(
            [new ErroDeLinha(1, "nome", "Coluna repetida."), new ErroDeLinha(1, "cor", "Coluna desconhecida. As colunas do modelo são: tipo, codigo, nome, preco, categoria, grupo, minimo, maximo.")],
            plano.Erros);
        Assert.False(plano.Valido);
        Assert.Empty(plano.Itens);

        var semNome = RegrasDaImportacao.Planejar(Planilha("tipo;codigo"), Vazio);
        Assert.Equal([new ErroDeLinha(1, "nome", "A coluna obrigatória não está na planilha.")], semNome.Erros);

        var vazia = RegrasDaImportacao.Planejar([], Vazio);
        Assert.Equal([new ErroDeLinha(1, "", "A planilha está vazia.")], vazia.Erros);
    }

    [Fact]
    public void A_sheet_with_a_header_only_is_valid_and_does_nothing()
    {
        var plano = RegrasDaImportacao.Planejar(ComCabecalho(), Vazio);

        Assert.True(plano.Valido);
        Assert.Empty(plano.Itens);
    }

    [Fact]
    public void Every_bad_row_is_reported_with_its_line_column_and_message_in_one_pass_in_line_order()
    {
        const string nomeProduto = "O nome é obrigatório (até 120 caracteres, sem caracteres de controle).";
        const string codigo = "O código curto é obrigatório (1 a 20 de A-Z, 0-9 e hífen, sem começar por hífen).";
        const string nomeCurto = "O nome é obrigatório (até 80 caracteres, sem caracteres de controle).";
        const string precoInvalido = "Preço inválido. Use reais com até duas casas, por exemplo 25,90.";
        var plano = RegrasDaImportacao.Planejar(
            ComCabecalho(
                "bebida;;Suco;;;;;",
                "categoria;;;;;;;",
                "produto;;Suco;8;Bebidas;;;",
                "produto;a b;Suco;8;Bebidas;;;",
                "produto;SU1;;8;Bebidas;;;",
                "produto;SU2;Suco;oito;Bebidas;;;",
                "produto;SU3;Suco;;Bebidas;;;",
                "produto;SU4;Suco;8;Fantasma;;;",
                "produto;SU5;Suco;8;Bebidas;Inexistente;;",
                "produto;;;;;;;",
                "grupo;;Ponto;;;;um;2",
                "grupo;;Ponto2;;;;2;1",
                "grupo;;Ponto3;;;;;",
                "opcao;;Grande;3;;Fantasma;;",
                "opcao;;;3;;Tamanho;;",
                "opcao;;Grande;x;;Tamanho;;",
                "opcao;;Grande;3;;;;",
                "categoria;;Bebidas;;;;;",
                "grupo;;Tamanho;;;;1;2"),
            Vazio);

        Assert.Equal(
            [
                new ErroDeLinha(2, "tipo", "Tipo desconhecido. Use categoria, produto, grupo ou opcao."),
                new ErroDeLinha(3, "nome", nomeCurto),
                new ErroDeLinha(4, "codigo", codigo),
                new ErroDeLinha(5, "codigo", codigo),
                new ErroDeLinha(6, "nome", nomeProduto),
                new ErroDeLinha(7, "preco", precoInvalido),
                new ErroDeLinha(8, "preco", "O preço é obrigatório."),
                new ErroDeLinha(9, "categoria", "A categoria 'Fantasma' não existe no cardápio nem na planilha."),
                new ErroDeLinha(10, "grupo", "O grupo 'Inexistente' não existe no cardápio nem na planilha."),
                new ErroDeLinha(11, "codigo", codigo),
                new ErroDeLinha(11, "nome", nomeProduto),
                new ErroDeLinha(11, "preco", "O preço é obrigatório."),
                new ErroDeLinha(12, "minimo", "O mínimo deve ser um número inteiro."),
                new ErroDeLinha(13, "maximo", "Limites inválidos: o mínimo não pode passar do máximo e o máximo vai de 1 a 50."),
                new ErroDeLinha(14, "minimo", "O mínimo é obrigatório."),
                new ErroDeLinha(14, "maximo", "O máximo é obrigatório."),
                new ErroDeLinha(15, "grupo", "O grupo 'Fantasma' não existe no cardápio nem na planilha."),
                new ErroDeLinha(16, "nome", nomeCurto),
                new ErroDeLinha(17, "preco", precoInvalido),
                new ErroDeLinha(18, "grupo", "O grupo da opção é obrigatório."),
            ],
            plano.Erros);
        Assert.Empty(plano.Itens);
        Assert.False(plano.Valido);
    }

    [Fact]
    public void A_row_repeating_a_key_of_an_earlier_row_is_refused_naming_the_first_line()
    {
        var plano = RegrasDaImportacao.Planejar(
            ComCabecalho(
                "categoria;;Bebidas;;;;;",
                "categoria;;BEBIDAS;;;;;",
                "produto;SU1;Suco;8;Bebidas;;;",
                "produto;su1;Outro;9;Bebidas;;;",
                "grupo;;Tamanho;;;;0;1",
                "grupo;;tamanho;;;;0;1",
                "opcao;;Grande;1;;Tamanho;;",
                "opcao;;grande;2;;Tamanho;;"),
            Vazio);

        Assert.Equal(
            [
                new ErroDeLinha(3, "nome", "Categoria repetida (primeira vez na linha 2)."),
                new ErroDeLinha(5, "codigo", "Código repetido (primeira vez na linha 4)."),
                new ErroDeLinha(7, "nome", "Grupo repetido (primeira vez na linha 6)."),
                new ErroDeLinha(9, "nome", "Opção repetida no grupo (primeira vez na linha 8)."),
            ],
            plano.Erros);
    }

    [Fact]
    public void The_category_tree_keeps_two_levels_and_a_category_cannot_be_its_own_parent_or_have_a_missing_parent()
    {
        var raiz = new Categoria(Guid.CreateVersion7(), "Raiz", true, 1);
        var filha = new Categoria(Guid.CreateVersion7(), "Filha", true, 1, raiz.Id);
        var estado = new EstadoDoCardapio([raiz, filha], [], [], new Dictionary<Guid, IReadOnlyList<Guid>>());

        var plano = RegrasDaImportacao.Planejar(
            ComCabecalho(
                "categoria;;Neta;;Filha;;;",
                "categoria;;Sozinha;;Sozinha;;;",
                "categoria;;Orfa;;Nada;;;",
                "categoria;;Raiz;;Filha;;;"),
            estado);

        Assert.Equal(
            [
                new ErroDeLinha(2, "categoria", "Nível inválido: 'Filha' já é uma subcategoria e o cardápio tem no máximo dois níveis."),
                new ErroDeLinha(3, "categoria", "Uma categoria não pode ser pai dela mesma."),
                new ErroDeLinha(4, "categoria", "A categoria 'Nada' não existe no cardápio nem na planilha."),
                new ErroDeLinha(5, "categoria", "Nível inválido: 'Filha' já é uma subcategoria e o cardápio tem no máximo dois níveis."),
            ],
            plano.Erros);
    }

    [Fact]
    public void A_category_that_has_children_cannot_become_a_child()
    {
        var a = new Categoria(Guid.CreateVersion7(), "A", true, 1);
        var b = new Categoria(Guid.CreateVersion7(), "B", true, 1);
        var filhaDeA = new Categoria(Guid.CreateVersion7(), "FilhaDeA", true, 1, a.Id);
        var estado = new EstadoDoCardapio([a, b, filhaDeA], [], [], new Dictionary<Guid, IReadOnlyList<Guid>>());

        var plano = RegrasDaImportacao.Planejar(ComCabecalho("categoria;;A;;B;;;"), estado);
        var movendoOFilho = RegrasDaImportacao.Planejar(ComCabecalho("categoria;;A;;B;;;", "categoria;;FilhaDeA;;B;;;"), estado);

        Assert.Equal([new ErroDeLinha(2, "categoria", "Nível inválido: 'A' tem subcategorias e não pode virar subcategoria.")], plano.Erros);

        // The check is made on the result: when the child leaves in the same sheet, the parent has none left and may move.
        Assert.Empty(movendoOFilho.Erros);
        var movida = movendoOFilho.Categorias.Single(c => c.Categoria.Id == filhaDeA.Id);
        Assert.Equal((AcaoDaImportacao.Atualizar, 2, b.Id), (movida.Acao, movida.Categoria.Versao, movida.Categoria.CategoriaPaiId));
    }

    [Fact]
    public void A_lower_price_than_the_registered_promotion_is_refused_and_ambiguous_names_are_not_guessed()
    {
        var cat = new Categoria(Guid.CreateVersion7(), "Lanches", true, 1);
        var dup1 = new Categoria(Guid.CreateVersion7(), "Doces", true, 1);
        var dup2 = new Categoria(Guid.CreateVersion7(), "doces", true, 1);
        var promo = new Promocao(1500, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1));
        var produto = new Produto(Guid.CreateVersion7(), cat.Id, "HAMB", "Hamburguer", 2000, "BRL", true, 1, false, promo);
        var estado = new EstadoDoCardapio([cat, dup1, dup2], [produto], [], new Dictionary<Guid, IReadOnlyList<Guid>>());

        var plano = RegrasDaImportacao.Planejar(ComCabecalho("produto;HAMB;Hamburguer;14;;;;", "produto;SU1;Suco;8;Doces;;;"), estado);

        Assert.Equal(
            [
                new ErroDeLinha(2, "preco", "O preço fica abaixo do preço promocional cadastrado; ajuste a promoção antes."),
                new ErroDeLinha(3, "categoria", "Mais de uma categoria do cardápio atual tem o nome 'Doces'; renomeie antes de importar."),
            ],
            plano.Erros);
    }

    [Fact]
    public void An_existing_product_whose_links_already_match_the_sheet_is_unchanged_and_a_different_list_replaces_them_in_order()
    {
        var cat = new Categoria(Guid.CreateVersion7(), "Lanches", true, 1);
        var g1 = new GrupoDeOpcao(Guid.CreateVersion7(), "Um", 0, 1, true, 1, []);
        var g2 = new GrupoDeOpcao(Guid.CreateVersion7(), "Dois", 0, 1, true, 1, []);
        var produto = new Produto(Guid.CreateVersion7(), cat.Id, "HAMB", "Hamburguer", 2000, "BRL", true, 1);
        var vinculos = new Dictionary<Guid, IReadOnlyList<Guid>> { [produto.Id] = [g1.Id, g2.Id] };
        var estado = new EstadoDoCardapio([cat], [produto], [g1, g2], vinculos);

        var igual = RegrasDaImportacao.Planejar(ComCabecalho("produto;HAMB;Hamburguer;20;;Um| Dois ;;"), estado);
        var troca = RegrasDaImportacao.Planejar(ComCabecalho("produto;HAMB;Hamburguer;20;;Dois|Um;;"), estado);
        var repetido = RegrasDaImportacao.Planejar(ComCabecalho("produto;HAMB;Hamburguer;20;;Um|um;;"), estado);

        Assert.Equal(AcaoDaImportacao.SemMudanca, igual.Produtos[0].Acao);
        Assert.Equal((AcaoDaImportacao.Atualizar, 1), (troca.Produtos[0].Acao, troca.Produtos[0].Produto.Versao));
        Assert.Equal([g2.Id, g1.Id], troca.Produtos[0].GrupoIds);
        Assert.Equal([new ErroDeLinha(2, "grupo", "O grupo 'um' aparece mais de uma vez na lista.")], repetido.Erros);
    }

    [Fact]
    public void A_group_cannot_hold_more_options_than_the_limit()
    {
        var linhas = ComCabecalho([
            "grupo;;Muitos;;;;0;1",
            .. Enumerable.Range(1, RegrasDeOpcoes.OpcoesMaximasPorGrupo + 1).Select(i => $"opcao;;Opcao {i};1;;Muitos;;"),
        ]);

        var plano = RegrasDaImportacao.Planejar(linhas, Vazio);

        Assert.Equal([new ErroDeLinha(linhas[^1].Numero, "nome", "O grupo passaria de 50 opções.")], plano.Erros);
    }

    [Fact]
    public void A_sheet_past_the_row_limit_is_refused_before_anything_else()
    {
        var linhas = ComCabecalho([.. Enumerable.Repeat("categoria;;A;;;;;", ModeloDaImportacao.LinhasMaximas + 1)]);

        var plano = RegrasDaImportacao.Planejar(linhas, Vazio);

        Assert.Equal([new ErroDeLinha(1, "", "A planilha passa do limite de 2000 linhas de dados.")], plano.Erros);
    }

    [Fact]
    public void The_example_of_the_template_is_a_valid_sheet()
    {
        var linhas = ModeloDaImportacao.ExemploCsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select((l, i) => new LinhaDaPlanilha(i + 1, l.Split(';'))).ToList();

        var plano = RegrasDaImportacao.Planejar(linhas, Vazio);

        Assert.Empty(plano.Erros);
        Assert.NotEmpty(plano.Itens);
    }
}
