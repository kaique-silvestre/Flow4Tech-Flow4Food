using Flow4Tech.Cardapio.Domain;
using Flow4Tech.Kernel.Audit;
using Flow4Tech.Kernel.Authorization;
using Flow4Tech.Kernel.Events;

namespace Flow4Tech.Cardapio.Application;

[PermissionCatalog]
public static class ImportacaoPermissions
{
    /// <summary>Import the menu from a spreadsheet: preview and apply. Audited. Changing the price of a product that already exists still asks the engine for <c>cardapio:preco-alterar</c>.</summary>
    public const string Importar = "cardapio:importar";
}

[AuditCatalog]
public static class ImportacaoAuditActions
{
    public const string Importar = "cardapio:importar";
}

/// <summary>Raised once per applied import (counts only), on the same unit of work.</summary>
public sealed record CardapioImportado(Guid EventId, Guid ImportacaoId, ResumoDaImportacao Resumo) : IDomainEvent
{
    public const string Type = "cardapio.importado";

    public string EventType => Type;
}

public enum FormatoDePlanilha
{
    Csv,

    Xlsx,
}

public enum ErroDeLeitura
{
    Nenhum,

    /// <summary>Not a readable file of the declared format (bad zip or XML, bad text, no sheet).</summary>
    FormatoInvalido,

    ArquivoGrande,

    LinhasDemais,

    /// <summary>A workbook that carries macros (a VBA project). Never opened.</summary>
    ComMacro,

    /// <summary>A cell with a formula. Formulas are never executed, so the sheet is refused (the line says where).</summary>
    ComFormula,
}

public sealed record LeituraDaPlanilha(ErroDeLeitura Erro, IReadOnlyList<LinhaDaPlanilha> Linhas, int Linha = 0);

/// <summary>Port: turns the bytes of a CSV or XLSX file into rows of text. Reads values only (no formula is evaluated, no macro is touched) and enforces the size and row limits.</summary>
public interface ILeitorDePlanilha
{
    LeituraDaPlanilha Ler(FormatoDePlanilha formato, byte[] conteudo);
}

/// <summary>An applied import. <paramref name="Hash"/> fingerprints the file so the same id sent with another file is told apart from a repeat.</summary>
public sealed record ImportacaoRegistrada(Guid Id, string Hash, ResumoDaImportacao Resumo, Guid CriadoPor, DateTimeOffset CriadoEm);

/// <summary>Port: the append-only record of applied imports (idempotency by import id). Runs inside a unit of work.</summary>
public interface IImportacoesDoCardapioStore
{
    Task<ImportacaoRegistrada?> AcharAsync(Guid organizationId, Guid id, CancellationToken cancellationToken);

    /// <summary>Records the import; false when the id is already taken (an earlier or concurrent import won).</summary>
    Task<bool> RegistrarAsync(Guid organizationId, ImportacaoRegistrada importacao, CancellationToken cancellationToken);
}

public enum ResultadoDaImportacao
{
    /// <summary>Read and planned (the preview), or read, planned and written (the apply).</summary>
    Ok,

    /// <summary>The apply found the id already applied with the same file; nothing was written again.</summary>
    JaAplicada,

    /// <summary>The file could not be read (see the reading in the result).</summary>
    ArquivoInvalido,

    /// <summary>At least one row is wrong; nothing was written.</summary>
    ComErros,

    /// <summary>The id was already used for a different file.</summary>
    IdReutilizado,

    /// <summary>The engine refused the price change of an existing product; nothing was written. Answered as 403 without reason.</summary>
    Negado,

    /// <summary>The menu changed between reading it and writing; nothing was written. Preview again.</summary>
    Desatualizada,

    Invalido,
}

public sealed record ResultadoDaValidacao(ResultadoDaImportacao Resultado, LeituraDaPlanilha? Leitura = null, PlanoDaImportacao? Plano = null);

public sealed record ResultadoDaAplicacao(ResultadoDaImportacao Resultado, ResumoDaImportacao? Resumo = null, LeituraDaPlanilha? Leitura = null, PlanoDaImportacao? Plano = null);
