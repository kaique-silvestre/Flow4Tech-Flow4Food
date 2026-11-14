using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Flow4Tech.Cardapio.Application;
using Flow4Tech.Cardapio.Domain;

namespace Flow4Tech.Cardapio.Infrastructure;

/// <summary>
/// Reads the owner's sheet as rows of text, CSV or XLSX behind one port. Values only: a formula is never evaluated (a formula cell refuses the whole file, saying the line), a workbook with a VBA
/// project is never opened, and the size and row limits of the template are enforced while reading (a zip is read through a bounded buffer, so a small file cannot inflate without limit).
/// The standard library does the work (<c>System.IO.Compression</c>, <c>System.Xml</c>); there is no spreadsheet package.
/// </summary>
public sealed class LeitorDePlanilha : ILeitorDePlanilha
{
    private const int LimiteDaParte = 2 * 1024 * 1024;

    private const int LimiteTotal = 6 * 1024 * 1024;

    private const int LetrasMaximas = 3;

    private const int ColunasMaximas = 40;

    private const int EntradasMaximas = 200;

    public LeituraDaPlanilha Ler(FormatoDePlanilha formato, byte[] conteudo)
    {
        ArgumentNullException.ThrowIfNull(conteudo);
        if (conteudo.Length > ModeloDaImportacao.BytesMaximos)
        {
            return Falha(ErroDeLeitura.ArquivoGrande);
        }

        try
        {
            return formato == FormatoDePlanilha.Xlsx ? LerXlsx(conteudo) : LerCsv(conteudo);
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or FormatException or IOException or InvalidOperationException or OverflowException or ArgumentException)
        {
            return Falha(ErroDeLeitura.FormatoInvalido);
        }
    }

    private static LeituraDaPlanilha Falha(ErroDeLeitura erro, int linha = 0) => new(erro, [], linha);

    // ---- CSV -------------------------------------------------------------------------------------------------------------------------------

    private static LeituraDaPlanilha LerCsv(byte[] conteudo)
    {
        string texto;
        try
        {
            texto = new UTF8Encoding(false, true).GetString(conteudo);
        }
        catch (DecoderFallbackException)
        {
            // A "CSV" saved by a Brazilian Excel is usually Windows-1252, not UTF-8.
            texto = Encoding.Latin1.GetString(conteudo);
        }

        texto = texto.TrimStart('﻿');
        var primeira = texto.Split('\n', 2)[0];
        var separador = primeira.Count(c => c == ';') >= primeira.Count(c => c == ',') && primeira.Contains(';', StringComparison.Ordinal) ? ';' : ',';

        var linhas = new List<LinhaDaPlanilha>();
        var celulas = new List<string>();
        var atual = new StringBuilder();
        var entreAspas = false;
        var numero = 1;
        var inicio = 1;
        for (var i = 0; i < texto.Length; i++)
        {
            var c = texto[i];
            if (entreAspas)
            {
                if (c == '"' && i + 1 < texto.Length && texto[i + 1] == '"')
                {
                    atual.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    entreAspas = false;
                }
                else
                {
                    atual.Append(c);
                    numero += c == '\n' ? 1 : 0;
                }

                continue;
            }

            if (c == '"' && atual.Length == 0)
            {
                entreAspas = true;
            }
            else if (c == separador)
            {
                celulas.Add(atual.ToString());
                atual.Clear();
            }
            else if (c == '\n')
            {
                if (FecharLinha(linhas, celulas, atual, inicio) is { } erro)
                {
                    return erro;
                }

                numero++;
                inicio = numero;
            }
            else if (c != '\r')
            {
                atual.Append(c);
            }
        }

        return entreAspas ? Falha(ErroDeLeitura.FormatoInvalido) : FecharLinha(linhas, celulas, atual, inicio) ?? new LeituraDaPlanilha(ErroDeLeitura.Nenhum, linhas);
    }

    /// <summary>Ends the record being read: null when it was added (or was blank), the refusal when it breaks a limit or carries a formula.</summary>
    private static LeituraDaPlanilha? FecharLinha(List<LinhaDaPlanilha> linhas, List<string> celulas, StringBuilder atual, int numero)
    {
        celulas.Add(atual.ToString());
        atual.Clear();
        var linha = new List<string>(celulas);
        celulas.Clear();
        if (linha.All(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        if (linha.Any(c => c.TrimStart().StartsWith('=')))
        {
            return Falha(ErroDeLeitura.ComFormula, numero);
        }

        linhas.Add(new LinhaDaPlanilha(numero, linha));
        return linhas.Count > ModeloDaImportacao.LinhasMaximas + 1 ? Falha(ErroDeLeitura.LinhasDemais) : null;
    }

    // ---- XLSX ------------------------------------------------------------------------------------------------------------------------------

    private static LeituraDaPlanilha LerXlsx(byte[] conteudo)
    {
        var gasto = new Gasto();
        using var zip = new ZipArchive(new MemoryStream(conteudo), ZipArchiveMode.Read);
        if (zip.Entries.Count > EntradasMaximas)
        {
            return Falha(ErroDeLeitura.FormatoInvalido);
        }

        if (zip.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)) || Texto(zip, "[Content_Types].xml", gasto)?.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) == true)
        {
            return Falha(ErroDeLeitura.ComMacro);
        }

        var caminho = CaminhoDaPrimeiraPlanilha(zip, gasto);
        var xml = caminho is null ? null : Documento(zip, caminho, gasto);
        if (xml is null)
        {
            return Falha(ErroDeLeitura.FormatoInvalido);
        }

        var compartilhadas = Documento(zip, "xl/sharedStrings.xml", gasto) is { } ss
            ? ss.Descendants().Where(e => e.Name.LocalName == "si").Select(TextoDe).ToList()
            : [];
        var linhas = new List<LinhaDaPlanilha>();
        var sequencial = 0;
        foreach (var row in xml.Descendants().Where(e => e.Name.LocalName == "row"))
        {
            sequencial++;
            var numero = int.TryParse((string?)row.Attribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : sequencial;
            var celulas = new List<string>();
            foreach (var c in row.Elements().Where(e => e.Name.LocalName == "c"))
            {
                if (c.Elements().Any(e => e.Name.LocalName == "f"))
                {
                    return Falha(ErroDeLeitura.ComFormula, numero);
                }

                var coluna = ColunaDe((string?)c.Attribute("r"), celulas.Count);
                if (coluna >= ColunasMaximas)
                {
                    continue;
                }

                while (celulas.Count <= coluna)
                {
                    celulas.Add(string.Empty);
                }

                celulas[coluna] = Valor(c, compartilhadas);
            }

            if (celulas.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            linhas.Add(new LinhaDaPlanilha(numero, celulas));
            if (linhas.Count > ModeloDaImportacao.LinhasMaximas + 1)
            {
                return Falha(ErroDeLeitura.LinhasDemais);
            }
        }

        return new LeituraDaPlanilha(ErroDeLeitura.Nenhum, linhas);
    }

    private static string Valor(XElement celula, List<string> compartilhadas)
    {
        var tipo = (string?)celula.Attribute("t");
        var v = celula.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
        switch (tipo)
        {
            case "s":
                return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < compartilhadas.Count ? compartilhadas[i] : string.Empty;
            case "inlineStr":
                return celula.Elements().FirstOrDefault(e => e.Name.LocalName == "is") is { } inline ? TextoDe(inline) : string.Empty;
            case "b":
                return v == "1" ? "TRUE" : "FALSE";
            case "str" or "e":
                return v;
            default:
                // A number: Excel may store 12.1 as 12.0999999999999996. Read as decimal (never a float) and keep four places, so "12.1" comes out.
                return decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Math.Round(d, 4).ToString("0.####", CultureInfo.InvariantCulture) : v;
        }
    }

    private static string TextoDe(XElement e) =>
        string.Concat(e.Descendants().Where(t => t.Name.LocalName == "t" && !t.Ancestors().Any(a => a.Name.LocalName == "rPh")).Select(t => t.Value));

    private static int ColunaDe(string? referencia, int proxima)
    {
        if (string.IsNullOrEmpty(referencia))
        {
            return proxima;
        }

        var coluna = 0;
        var letras = referencia.TakeWhile(char.IsAsciiLetter).ToList();
        if (letras.Count > LetrasMaximas)
        {
            // No template column is beyond "ZZZ"; a longer reference would overflow the arithmetic below.
            throw new InvalidDataException("Cell reference too wide.");
        }

        foreach (var c in letras)
        {
            coluna = (coluna * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return coluna == 0 ? proxima : coluna - 1;
    }

    private static string? CaminhoDaPrimeiraPlanilha(ZipArchive zip, Gasto gasto)
    {
        var relacao = Documento(zip, "xl/workbook.xml", gasto)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "sheet")?.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
        var destino = relacao is null
            ? null
            : Documento(zip, "xl/_rels/workbook.xml.rels", gasto)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Relationship" && (string?)e.Attribute("Id") == relacao)?.Attribute("Target")?.Value;
        if (destino is null)
        {
            return zip.GetEntry("xl/worksheets/sheet1.xml") is null ? null : "xl/worksheets/sheet1.xml";
        }

        return destino.StartsWith('/') ? destino[1..] : "xl/" + destino;
    }

    private static string? Texto(ZipArchive zip, string caminho, Gasto gasto) => Bytes(zip, caminho, gasto) is { } b ? Encoding.UTF8.GetString(b) : null;

    private static XElement? Documento(ZipArchive zip, string caminho, Gasto gasto)
    {
        if (Bytes(zip, caminho, gasto) is not { } bytes)
        {
            return null;
        }

        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = LimiteDaParte * 2L });
        return XElement.Load(reader);
    }

    /// <summary>Bytes inflated so far by one read of one workbook (all parts together).</summary>
    private sealed class Gasto
    {
        public long Total { get; set; }
    }

    /// <summary>The entry through a bounded buffer: more than the limit means the file inflates too much to be a menu sheet.</summary>
    private static byte[]? Bytes(ZipArchive zip, string caminho, Gasto gasto)
    {
        var entrada = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, caminho, StringComparison.OrdinalIgnoreCase));
        if (entrada is null)
        {
            return null;
        }

        using var origem = entrada.Open();
        using var destino = new MemoryStream();
        var buffer = new byte[81920];
        int lidos;
        while ((lidos = origem.Read(buffer, 0, buffer.Length)) > 0)
        {
            destino.Write(buffer, 0, lidos);
            gasto.Total += lidos;
            if (destino.Length > LimiteDaParte || gasto.Total > LimiteTotal)
            {
                throw new InvalidDataException("Sheet part too large.");
            }
        }

        return destino.ToArray();
    }
}
