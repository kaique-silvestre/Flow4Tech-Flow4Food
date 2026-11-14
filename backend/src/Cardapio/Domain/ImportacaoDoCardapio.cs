using System.Globalization;
using Flow4Tech.Kernel;

namespace Flow4Tech.Cardapio.Domain;

/// <summary>One non-parsed row of the sheet: its number in the file (what the owner sees) and its cells, left to right.</summary>
public sealed record LinhaDaPlanilha(int Numero, IReadOnlyList<string> Celulas);

/// <summary>A problem found in a row: the line, the template column it is about ("" when it is about the whole sheet) and a message the owner can act on.</summary>
public sealed record ErroDeLinha(int Linha, string Coluna, string Mensagem);

public enum AcaoDaImportacao
{
    Criar,

    Atualizar,

    SemMudanca,
}

/// <summary>What one data row of the sheet does. <paramref name="Tipo"/> is categoria, produto, grupo or opcao; <paramref name="Chave"/> is what the row is matched by.</summary>
public sealed record ItemDaPrevia(string Tipo, string Chave, AcaoDaImportacao Acao, int Linha);

/// <summary>Counts only (they are what the audit record and the event carry): nothing of the menu itself.</summary>
public sealed record ResumoDaImportacao(
    int CategoriasCriadas,
    int CategoriasAtualizadas,
    int ProdutosCriados,
    int ProdutosAtualizados,
    int GruposCriados,
    int GruposAtualizados,
    int OpcoesCriadas,
    int OpcoesAtualizadas);

/// <summary>The menu as it is now: what the sheet is matched against. <paramref name="VinculosDosProdutos"/> maps a product id to its option group ids, in order.</summary>
public sealed record EstadoDoCardapio(
    IReadOnlyList<Categoria> Categorias,
    IReadOnlyList<Produto> Produtos,
    IReadOnlyList<GrupoDeOpcao> Grupos,
    IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> VinculosDosProdutos);

/// <summary>A category after the import (new, or the stored one with its new parent and version).</summary>
public sealed record CategoriaNoPlano(Categoria Categoria, AcaoDaImportacao Acao);

/// <summary>A product after the import. <paramref name="GrupoIds"/> is null when the links stay as they are, otherwise the full ordered list to store.</summary>
public sealed record ProdutoNoPlano(Produto Produto, IReadOnlyList<Guid>? GrupoIds, AcaoDaImportacao Acao);

/// <summary>A group after the import, with ALL its options (the stored ones, changed or not, then the new ones). Options the sheet does not mention are never turned off.</summary>
public sealed record GrupoNoPlano(GrupoDeOpcao Grupo, AcaoDaImportacao Acao, int OpcoesCriadas, int OpcoesAtualizadas);

/// <summary>The outcome of reading the sheet against the menu: either errors (and nothing to do) or what to create and update.</summary>
public sealed record PlanoDaImportacao(
    IReadOnlyList<ErroDeLinha> Erros,
    IReadOnlyList<ItemDaPrevia> Itens,
    IReadOnlyList<CategoriaNoPlano> Categorias,
    IReadOnlyList<ProdutoNoPlano> Produtos,
    IReadOnlyList<GrupoNoPlano> Grupos)
{
    public bool Valido => Erros.Count == 0;

    public ResumoDaImportacao Resumo => new(
        Categorias.Count(c => c.Acao == AcaoDaImportacao.Criar),
        Categorias.Count(c => c.Acao == AcaoDaImportacao.Atualizar),
        Produtos.Count(p => p.Acao == AcaoDaImportacao.Criar),
        Produtos.Count(p => p.Acao == AcaoDaImportacao.Atualizar),
        Grupos.Count(g => g.Acao == AcaoDaImportacao.Criar),
        Grupos.Count(g => g.Acao == AcaoDaImportacao.Atualizar),
        Grupos.Sum(g => g.OpcoesCriadas),
        Grupos.Sum(g => g.OpcoesAtualizadas));
}

/// <summary>The sheet template: one header row and one row per category, product, option group or option, told apart by the <c>tipo</c> column.</summary>
public static class ModeloDaImportacao
{
    public const string Tipo = "tipo";

    public const string Codigo = "codigo";

    public const string Nome = "nome";

    public const string Preco = "preco";

    public const string Categoria = "categoria";

    public const string Grupo = "grupo";

    public const string Minimo = "minimo";

    public const string Maximo = "maximo";

    public const int LinhasMaximas = 2000;

    public const int BytesMaximos = 1_048_576;

    public static IReadOnlyList<string> Colunas { get; } = [Tipo, Codigo, Nome, Preco, Categoria, Grupo, Minimo, Maximo];

    public static IReadOnlyList<string> Tipos { get; } = ["categoria", "produto", "grupo", "opcao"];

    /// <summary>A small valid sheet (CSV with ";", Brazilian style) to download as a starting point.</summary>
    public const string ExemploCsv =
        "tipo;codigo;nome;preco;categoria;grupo;minimo;maximo\r\n"
        + "categoria;;Bebidas;;;;;\r\n"
        + "categoria;;Cervejas;;Bebidas;;;\r\n"
        + "grupo;;Tamanho;;;;1;1\r\n"
        + "opcao;;Pequeno;0;;Tamanho;;\r\n"
        + "opcao;;Grande;3,50;;Tamanho;;\r\n"
        + "produto;CH01;Chopp;9,50;Cervejas;Tamanho;;\r\n";
}

/// <summary>
/// Pure rules of the menu import. Every row is checked, every problem is reported with its line and column, and nothing is planned unless the whole sheet is valid. Rows are matched to the
/// stored menu by short code (products) and by name ignoring case and accents (categories, groups, options within a group), so importing the same sheet twice changes nothing the second time.
/// A cell left blank keeps what an existing row has; the sheet never deletes or turns anything off.
/// </summary>
public static class RegrasDaImportacao
{
    private const string Categoria = "categoria";
    private const string Produto = "produto";
    private const string Grupo = "grupo";
    private const string Opcao = "opcao";

    /// <summary>"25,90", "25.9" or "R$ 10" as integer cents; null when it is not money with at most two decimals or is above the menu's price ceiling. Never goes through a float.</summary>
    public static long? Preco(string? texto)
    {
        var t = texto?.Trim() ?? string.Empty;
        if (t.StartsWith("R$", StringComparison.OrdinalIgnoreCase))
        {
            t = t[2..].Trim();
        }

        var separador = t.IndexOfAny([',', '.']);
        var inteiro = separador < 0 ? t : t[..separador];
        var fracao = separador < 0 ? string.Empty : t[(separador + 1)..];
        if (inteiro.Length is 0 or > 12 || fracao.Length > 2 || (separador >= 0 && fracao.Length == 0) || !(inteiro + fracao).All(char.IsAsciiDigit))
        {
            return null;
        }

        var centavos = (long.Parse(inteiro, CultureInfo.InvariantCulture) * 100) + (fracao.Length == 0 ? 0 : long.Parse(fracao.PadRight(2, '0'), CultureInfo.InvariantCulture));
        return RegrasDoCardapio.PrecoValido(centavos) ? centavos : null;
    }

    public static PlanoDaImportacao Planejar(IReadOnlyList<LinhaDaPlanilha> linhas, EstadoDoCardapio atual)
    {
        ArgumentNullException.ThrowIfNull(linhas);
        ArgumentNullException.ThrowIfNull(atual);
        var dados = linhas.Where(l => l.Celulas.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (dados.Count == 0)
        {
            return Falha(new ErroDeLinha(1, string.Empty, "A planilha está vazia."));
        }

        if (dados.Count - 1 > ModeloDaImportacao.LinhasMaximas)
        {
            return Falha(new ErroDeLinha(1, string.Empty, $"A planilha passa do limite de {ModeloDaImportacao.LinhasMaximas} linhas de dados."));
        }

        var erros = new List<ErroDeLinha>();
        var colunas = LerCabecalho(dados[0], erros);
        if (erros.Count > 0)
        {
            return Falha([.. erros]);
        }

        var categorias = new List<LinhaCategoria>();
        var produtos = new List<LinhaProduto>();
        var grupos = new List<LinhaGrupo>();
        var opcoes = new List<LinhaOpcao>();
        foreach (var linha in dados.Skip(1))
        {
            LerLinha(new Celulas(linha, colunas), erros, categorias, produtos, grupos, opcoes);
        }

        var itens = new List<ItemDaPrevia>();
        var resolvido = new Resolucao(atual, erros, itens);
        resolvido.Categorias(categorias);
        resolvido.Grupos(grupos, opcoes);
        resolvido.Produtos(produtos);
        if (erros.Count > 0)
        {
            return Falha([.. erros.OrderBy(e => e.Linha)]);
        }

        return new PlanoDaImportacao([], [.. itens.OrderBy(i => i.Linha)], resolvido.CategoriasNoPlano, resolvido.ProdutosNoPlano, resolvido.GruposNoPlano);
    }

    private static PlanoDaImportacao Falha(params ErroDeLinha[] erros) => new(erros, [], [], [], []);

    private static string Chave(string texto) => RegrasDeBusca.Normalizar(texto);

    private static Dictionary<string, int> LerCabecalho(LinhaDaPlanilha cabecalho, List<ErroDeLinha> erros)
    {
        var colunas = new Dictionary<string, int>();
        for (var i = 0; i < cabecalho.Celulas.Count; i++)
        {
            var texto = cabecalho.Celulas[i].Trim();
            if (texto.Length == 0)
            {
                continue;
            }

            var nome = ModeloDaImportacao.Colunas.FirstOrDefault(c => Chave(c) == Chave(texto));
            if (nome is null)
            {
                erros.Add(new ErroDeLinha(cabecalho.Numero, texto, $"Coluna desconhecida. As colunas do modelo são: {string.Join(", ", ModeloDaImportacao.Colunas)}."));
            }
            else if (!colunas.TryAdd(nome, i))
            {
                erros.Add(new ErroDeLinha(cabecalho.Numero, nome, "Coluna repetida."));
            }
        }

        foreach (var obrigatoria in new[] { ModeloDaImportacao.Tipo, ModeloDaImportacao.Nome }.Where(c => !colunas.ContainsKey(c)))
        {
            erros.Add(new ErroDeLinha(cabecalho.Numero, obrigatoria, "A coluna obrigatória não está na planilha."));
        }

        return colunas;
    }

    private static void LerLinha(Celulas c, List<ErroDeLinha> erros, List<LinhaCategoria> categorias, List<LinhaProduto> produtos, List<LinhaGrupo> grupos, List<LinhaOpcao> opcoes)
    {
        var antes = erros.Count;
        switch (Chave(c.Valor(ModeloDaImportacao.Tipo)))
        {
            case "CATEGORIA":
                var nomeDaCategoria = Texto(c, RegrasDoCardapio.NomeDeCategoria, RegrasDoCardapio.CategoriaNomeMaximo, erros);
                if (erros.Count == antes)
                {
                    categorias.Add(new LinhaCategoria(c.Linha, nomeDaCategoria!, Vazio(c.Valor(ModeloDaImportacao.Categoria))));
                }

                break;
            case "PRODUTO":
                var codigo = RegrasDoCardapio.Codigo(c.Valor(ModeloDaImportacao.Codigo));
                if (codigo is null)
                {
                    c.Erro(erros, ModeloDaImportacao.Codigo, "O código curto é obrigatório (1 a 20 de A-Z, 0-9 e hífen, sem começar por hífen).");
                }

                var nomeDoProduto = Texto(c, RegrasDoCardapio.NomeDeProduto, RegrasDoCardapio.NomeMaximo, erros);
                var preco = PrecoDaLinha(c, obrigatorio: true, erros);
                var lista = Vazio(c.Valor(ModeloDaImportacao.Grupo))?.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (erros.Count == antes)
                {
                    produtos.Add(new LinhaProduto(c.Linha, codigo!, nomeDoProduto!, preco, Vazio(c.Valor(ModeloDaImportacao.Categoria)), lista));
                }

                break;
            case "GRUPO":
                var nomeDoGrupo = Texto(c, RegrasDeOpcoes.NomeDoGrupo, RegrasDeOpcoes.NomeMaximo, erros);
                var minimo = Inteiro(c, ModeloDaImportacao.Minimo, "O mínimo", erros);
                var maximo = Inteiro(c, ModeloDaImportacao.Maximo, "O máximo", erros);
                if (erros.Count == antes && !RegrasDeOpcoes.LimitesValidos(minimo, maximo))
                {
                    c.Erro(erros, ModeloDaImportacao.Maximo, $"Limites inválidos: o mínimo não pode passar do máximo e o máximo vai de 1 a {RegrasDeOpcoes.MaximoDeEscolhas}.");
                }

                if (erros.Count == antes)
                {
                    grupos.Add(new LinhaGrupo(c.Linha, nomeDoGrupo!, minimo, maximo));
                }

                break;
            case "OPCAO":
                var nomeDaOpcao = Texto(c, RegrasDeOpcoes.NomeDaOpcao, RegrasDeOpcoes.NomeMaximo, erros);
                var precoDaOpcao = PrecoDaLinha(c, obrigatorio: false, erros);
                var grupoDaOpcao = Vazio(c.Valor(ModeloDaImportacao.Grupo));
                if (grupoDaOpcao is null)
                {
                    c.Erro(erros, ModeloDaImportacao.Grupo, "O grupo da opção é obrigatório.");
                }

                if (erros.Count == antes)
                {
                    opcoes.Add(new LinhaOpcao(c.Linha, grupoDaOpcao!, nomeDaOpcao!, precoDaOpcao));
                }

                break;
            default:
                c.Erro(erros, ModeloDaImportacao.Tipo, "Tipo desconhecido. Use categoria, produto, grupo ou opcao.");
                break;
        }
    }

    private static string? Vazio(string valor) => valor.Length == 0 ? null : valor;

    private static string? Texto(Celulas c, Func<string?, string?> regra, int maximo, List<ErroDeLinha> erros)
    {
        var nome = regra(c.Valor(ModeloDaImportacao.Nome));
        if (nome is null)
        {
            c.Erro(erros, ModeloDaImportacao.Nome, $"O nome é obrigatório (até {maximo} caracteres, sem caracteres de controle).");
        }

        return nome;
    }

    private static long PrecoDaLinha(Celulas c, bool obrigatorio, List<ErroDeLinha> erros)
    {
        var texto = c.Valor(ModeloDaImportacao.Preco);
        if (texto.Length == 0)
        {
            if (obrigatorio)
            {
                c.Erro(erros, ModeloDaImportacao.Preco, "O preço é obrigatório.");
            }

            return 0;
        }

        if (Preco(texto) is { } centavos)
        {
            return centavos;
        }

        c.Erro(erros, ModeloDaImportacao.Preco, "Preço inválido. Use reais com até duas casas, por exemplo 25,90.");
        return 0;
    }

    private static int Inteiro(Celulas c, string coluna, string rotulo, List<ErroDeLinha> erros)
    {
        var texto = c.Valor(coluna);
        if (texto.Length == 0)
        {
            c.Erro(erros, coluna, $"{rotulo} é obrigatório.");
            return 0;
        }

        if (texto.Length <= 3 && texto.All(char.IsAsciiDigit))
        {
            return int.Parse(texto, CultureInfo.InvariantCulture);
        }

        c.Erro(erros, coluna, $"{rotulo} deve ser um número inteiro.");
        return 0;
    }

    private sealed record LinhaCategoria(int Linha, string Nome, string? Pai);

    private sealed record LinhaProduto(int Linha, string Codigo, string Nome, long Preco, string? Categoria, string[]? Grupos);

    private sealed record LinhaGrupo(int Linha, string Nome, int Minimo, int Maximo);

    private sealed record LinhaOpcao(int Linha, string Grupo, string Nome, long Preco);

    private sealed class Celulas(LinhaDaPlanilha linha, Dictionary<string, int> colunas)
    {
        public int Linha => linha.Numero;

        public string Valor(string coluna) => colunas.TryGetValue(coluna, out var i) && i < linha.Celulas.Count ? linha.Celulas[i].Trim() : string.Empty;

        public void Erro(List<ErroDeLinha> erros, string coluna, string mensagem) => erros.Add(new ErroDeLinha(linha.Numero, coluna, mensagem));
    }

    /// <summary>The second pass: matches the valid rows against the stored menu and against each other (a name may be declared in the sheet or already stored).</summary>
    private sealed class Resolucao(EstadoDoCardapio atual, List<ErroDeLinha> erros, List<ItemDaPrevia> itens)
    {
        private readonly Dictionary<string, Guid> _categoriaIds = [];
        private readonly Dictionary<string, Guid> _grupoIds = [];
        private readonly Dictionary<Guid, Guid?> _paiDe = atual.Categorias.ToDictionary(c => c.Id, c => c.CategoriaPaiId);

        public List<CategoriaNoPlano> CategoriasNoPlano { get; } = [];

        public List<ProdutoNoPlano> ProdutosNoPlano { get; } = [];

        public List<GrupoNoPlano> GruposNoPlano { get; } = [];

        public void Categorias(List<LinhaCategoria> linhas)
        {
            var declaradas = new Dictionary<string, (LinhaCategoria Linha, Categoria? Antes, Guid Id)>();
            foreach (var l in linhas)
            {
                var chave = Chave(l.Nome);
                if (declaradas.TryGetValue(chave, out var primeira))
                {
                    erros.Add(new ErroDeLinha(l.Linha, ModeloDaImportacao.Nome, $"Categoria repetida (primeira vez na linha {primeira.Linha.Linha})."));
                    continue;
                }

                var existentes = atual.Categorias.Where(c => Chave(c.Nome) == chave).ToList();
                if (existentes.Count > 1)
                {
                    erros.Add(Ambigua(l.Linha, ModeloDaImportacao.Nome, "categoria", l.Nome));
                    continue;
                }

                var antes = existentes.FirstOrDefault();
                declaradas[chave] = (l, antes, antes?.Id ?? EntityId.New().Value);
                _categoriaIds[chave] = declaradas[chave].Id;
            }

            // Parents: found among the sheet's categories or the stored ones; the tree is checked on the RESULT so moving a whole branch in one sheet works.
            var pais = new Dictionary<string, Guid?>();
            foreach (var (chave, d) in declaradas)
            {
                pais[chave] = d.Antes?.CategoriaPaiId;
                if (d.Linha.Pai is not { } nomeDoPai)
                {
                    continue;
                }

                if (Chave(nomeDoPai) == chave)
                {
                    erros.Add(new ErroDeLinha(d.Linha.Linha, ModeloDaImportacao.Categoria, "Uma categoria não pode ser pai dela mesma."));
                }
                else if (Achar(nomeDoPai, _categoriaIds, atual.Categorias.Select(c => (c.Nome, c.Id)), "categoria", d.Linha.Linha, ModeloDaImportacao.Categoria) is { } pai)
                {
                    pais[chave] = pai;
                    _paiDe[d.Id] = pai;
                }
            }

            foreach (var (chave, d) in declaradas)
            {
                if (pais[chave] is { } pai && d.Linha.Pai is { } nomeDoPai && (d.Antes?.CategoriaPaiId != pai))
                {
                    if (_paiDe.GetValueOrDefault(pai) is not null)
                    {
                        erros.Add(new ErroDeLinha(d.Linha.Linha, ModeloDaImportacao.Categoria, $"Nível inválido: '{nomeDoPai}' já é uma subcategoria e o cardápio tem no máximo dois níveis."));
                    }
                    else if (_paiDe.Any(p => p.Value == d.Id))
                    {
                        erros.Add(new ErroDeLinha(d.Linha.Linha, ModeloDaImportacao.Categoria, $"Nível inválido: '{d.Linha.Nome}' tem subcategorias e não pode virar subcategoria."));
                    }
                }
            }

            foreach (var (chave, d) in declaradas)
            {
                var acao = d.Antes is null ? AcaoDaImportacao.Criar : pais[chave] == d.Antes.CategoriaPaiId ? AcaoDaImportacao.SemMudanca : AcaoDaImportacao.Atualizar;
                var categoria = d.Antes switch
                {
                    null => new Categoria(d.Id, d.Linha.Nome, true, 1, pais[chave]),
                    var antes when acao == AcaoDaImportacao.Atualizar => antes with { CategoriaPaiId = pais[chave], Versao = antes.Versao + 1 },
                    var antes => antes,
                };
                CategoriasNoPlano.Add(new CategoriaNoPlano(categoria, acao));
                itens.Add(new ItemDaPrevia(Categoria, d.Linha.Nome, acao, d.Linha.Linha));
            }
        }

        public void Grupos(List<LinhaGrupo> linhas, List<LinhaOpcao> opcoes)
        {
            var declarados = new Dictionary<string, (LinhaGrupo Linha, GrupoDeOpcao? Antes, Guid Id)>();
            foreach (var l in linhas)
            {
                var chave = Chave(l.Nome);
                if (declarados.TryGetValue(chave, out var primeiro))
                {
                    erros.Add(new ErroDeLinha(l.Linha, ModeloDaImportacao.Nome, $"Grupo repetido (primeira vez na linha {primeiro.Linha.Linha})."));
                    continue;
                }

                var existentes = atual.Grupos.Where(g => Chave(g.Nome) == chave).ToList();
                if (existentes.Count > 1)
                {
                    erros.Add(Ambigua(l.Linha, ModeloDaImportacao.Nome, "grupo", l.Nome));
                    continue;
                }

                var antes = existentes.FirstOrDefault();
                declarados[chave] = (l, antes, antes?.Id ?? EntityId.New().Value);
                _grupoIds[chave] = declarados[chave].Id;
            }

            var opcoesPorGrupo = new Dictionary<string, List<LinhaOpcao>>();
            foreach (var o in opcoes)
            {
                if (Achar(o.Grupo, _grupoIds, atual.Grupos.Select(g => (g.Nome, g.Id)), "grupo", o.Linha, ModeloDaImportacao.Grupo) is { } achado)
                {
                    var chave = Chave(o.Grupo);
                    _grupoIds.TryAdd(chave, achado);
                    if (!opcoesPorGrupo.TryGetValue(chave, out var lista))
                    {
                        opcoesPorGrupo[chave] = lista = [];
                    }

                    lista.Add(o);
                }
            }

            foreach (var (chave, lista) in opcoesPorGrupo.Where(p => !declarados.ContainsKey(p.Key)))
            {
                declarados[chave] = (new LinhaGrupo(0, lista[0].Grupo, 0, 0), atual.Grupos.Single(g => Chave(g.Nome) == chave), _grupoIds[chave]);
            }

            foreach (var (chave, d) in declarados)
            {
                MontarGrupo(chave, d.Linha, d.Antes, d.Id, opcoesPorGrupo.GetValueOrDefault(chave) ?? []);
            }
        }

        public void Produtos(List<LinhaProduto> linhas)
        {
            var vistos = new Dictionary<string, int>();
            foreach (var l in linhas)
            {
                if (!vistos.TryAdd(l.Codigo, l.Linha))
                {
                    erros.Add(new ErroDeLinha(l.Linha, ModeloDaImportacao.Codigo, $"Código repetido (primeira vez na linha {vistos[l.Codigo]})."));
                    continue;
                }

                var antes = atual.Produtos.FirstOrDefault(p => p.Codigo == l.Codigo);
                var categoriaId = l.Categoria is null ? antes?.CategoriaId : Achar(l.Categoria, _categoriaIds, atual.Categorias.Select(c => (c.Nome, c.Id)), "categoria", l.Linha, ModeloDaImportacao.Categoria);
                if (l.Categoria is null && antes is null)
                {
                    erros.Add(new ErroDeLinha(l.Linha, ModeloDaImportacao.Categoria, "A categoria é obrigatória para um produto novo."));
                }

                var vinculos = Vinculos(l);
                if (categoriaId is not { } categoria || (l.Grupos is not null && vinculos is null))
                {
                    continue;
                }

                if (antes is not null && l.Preco != antes.PrecoCentavos && !RegrasDePreco.PromocaoValida(antes.Promocao, l.Preco))
                {
                    erros.Add(new ErroDeLinha(l.Linha, ModeloDaImportacao.Preco, "O preço fica abaixo do preço promocional cadastrado; ajuste a promoção antes."));
                    continue;
                }

                var produto = antes is null
                    ? new Produto(EntityId.New().Value, categoria, l.Codigo, l.Nome, l.Preco, RegrasDoCardapio.MoedaPadrao, true, 1)
                    : antes with { Nome = l.Nome, PrecoCentavos = l.Preco, CategoriaId = categoria };
                var vinculosMudaram = vinculos is not null && !vinculos.SequenceEqual(atual.VinculosDosProdutos.GetValueOrDefault(produto.Id) ?? []);
                var acao = antes is null ? AcaoDaImportacao.Criar : produto == antes && !vinculosMudaram ? AcaoDaImportacao.SemMudanca : AcaoDaImportacao.Atualizar;
                if (acao == AcaoDaImportacao.Atualizar && produto != antes)
                {
                    produto = produto with { Versao = produto.Versao + 1 };
                }

                ProdutosNoPlano.Add(new ProdutoNoPlano(produto, vinculosMudaram || (antes is null && vinculos is not null) ? vinculos : null, acao));
                itens.Add(new ItemDaPrevia(Produto, l.Codigo, acao, l.Linha));
            }
        }

        private static ErroDeLinha Ambigua(int linha, string coluna, string tipo, string nome) =>
            new(linha, coluna, $"Mais de um{(tipo == "grupo" ? string.Empty : "a")} {tipo} do cardápio atual tem o nome '{nome}'; renomeie antes de importar.");

        /// <summary>The ordered group ids of a product row, or null (with the errors added) when a name is unknown or listed twice. Only called when the row has a group list.</summary>
        private List<Guid>? Vinculos(LinhaProduto l)
        {
            if (l.Grupos is null)
            {
                return null;
            }

            var ids = new List<Guid>();
            var ok = true;
            foreach (var nome in l.Grupos)
            {
                if (Achar(nome, _grupoIds, atual.Grupos.Select(g => (g.Nome, g.Id)), "grupo", l.Linha, ModeloDaImportacao.Grupo) is not { } id)
                {
                    ok = false;
                }
                else if (ids.Contains(id))
                {
                    erros.Add(new ErroDeLinha(l.Linha, ModeloDaImportacao.Grupo, $"O grupo '{nome}' aparece mais de uma vez na lista."));
                    ok = false;
                }
                else
                {
                    ids.Add(id);
                }
            }

            return ok ? ids : null;
        }

        private Guid? Achar(string nome, Dictionary<string, Guid> daPlanilha, IEnumerable<(string Nome, Guid Id)> guardados, string tipo, int linha, string coluna)
        {
            var chave = Chave(nome);
            if (daPlanilha.TryGetValue(chave, out var id))
            {
                return id;
            }

            var achados = guardados.Where(g => Chave(g.Nome) == chave).ToList();
            if (achados.Count == 1)
            {
                return achados[0].Id;
            }

            erros.Add(achados.Count > 1
                ? Ambigua(linha, coluna, tipo, nome)
                : new ErroDeLinha(linha, coluna, $"{(tipo == "grupo" ? "O" : "A")} {tipo} '{nome}' não existe no cardápio nem na planilha."));
            return null;
        }

        private void MontarGrupo(string chave, LinhaGrupo linha, GrupoDeOpcao? antes, Guid id, List<LinhaOpcao> doGrupo)
        {
            var lista = antes is null ? [] : antes.Opcoes.ToList();
            var vistas = new Dictionary<string, int>();
            int criadas = 0, atualizadas = 0;
            foreach (var o in doGrupo)
            {
                var chaveDaOpcao = Chave(o.Nome);
                if (!vistas.TryAdd(chaveDaOpcao, o.Linha))
                {
                    erros.Add(new ErroDeLinha(o.Linha, ModeloDaImportacao.Nome, $"Opção repetida no grupo (primeira vez na linha {vistas[chaveDaOpcao]})."));
                    continue;
                }

                var indice = lista.FindIndex(x => Chave(x.Nome) == chaveDaOpcao);
                AcaoDaImportacao acaoDaOpcao;
                if (indice < 0)
                {
                    lista.Add(new Opcao(EntityId.New().Value, o.Nome, o.Preco, true));
                    acaoDaOpcao = AcaoDaImportacao.Criar;
                    criadas++;
                }
                else
                {
                    var nova = lista[indice] with { PrecoCentavos = o.Preco, Ativa = true };
                    acaoDaOpcao = nova == lista[indice] ? AcaoDaImportacao.SemMudanca : AcaoDaImportacao.Atualizar;
                    atualizadas += acaoDaOpcao == AcaoDaImportacao.Atualizar ? 1 : 0;
                    lista[indice] = nova;
                }

                itens.Add(new ItemDaPrevia(Opcao, $"{linha.Nome} / {o.Nome}", acaoDaOpcao, o.Linha));
            }

            if (lista.Count > RegrasDeOpcoes.OpcoesMaximasPorGrupo)
            {
                erros.Add(new ErroDeLinha(doGrupo[^1].Linha, ModeloDaImportacao.Nome, $"O grupo passaria de {RegrasDeOpcoes.OpcoesMaximasPorGrupo} opções."));
                return;
            }

            var declarado = linha.Linha != 0;
            var limitesMudaram = antes is not null && declarado && (antes.Minimo != linha.Minimo || antes.Maximo != linha.Maximo);
            var acao = antes is null ? AcaoDaImportacao.Criar : limitesMudaram || criadas + atualizadas > 0 ? AcaoDaImportacao.Atualizar : AcaoDaImportacao.SemMudanca;
            var grupo = antes is null
                ? new GrupoDeOpcao(id, linha.Nome, linha.Minimo, linha.Maximo, true, 1, lista)
                : acao == AcaoDaImportacao.SemMudanca
                    ? antes
                    : antes with { Minimo = declarado ? linha.Minimo : antes.Minimo, Maximo = declarado ? linha.Maximo : antes.Maximo, Versao = antes.Versao + 1, Opcoes = lista };
            GruposNoPlano.Add(new GrupoNoPlano(grupo, acao, criadas, atualizadas));
            if (declarado)
            {
                itens.Add(new ItemDaPrevia(Grupo, linha.Nome, acao, linha.Linha));
            }
        }
    }
}
