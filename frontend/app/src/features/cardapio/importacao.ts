import type { components } from "@flow4tech/api-client";
import { uuidv7 } from "../comandas/uuid";
import { api } from "../sessao/sessao";

type Esquemas = components["schemas"];

export interface ErroDeLinha {
  linha: number;
  coluna: string;
  mensagem: string;
}

export interface Resumo {
  categoriasCriadas: number;
  categoriasAtualizadas: number;
  produtosCriados: number;
  produtosAtualizados: number;
  gruposCriados: number;
  gruposAtualizados: number;
  opcoesCriadas: number;
  opcoesAtualizadas: number;
}

export interface Previa {
  valida: boolean;
  erros: ErroDeLinha[];
  resumo: Resumo;
}

export interface Modelo {
  exemploCsv: string;
  bytesMaximos: number;
  linhasMaximas: number;
}

/** A chosen file: its content is read once, and its `importacaoId` is born with it (a resend is a repeat; another file is another import). */
export interface Arquivo {
  nome: string;
  formato: "csv" | "xlsx";
  conteudoBase64: string;
  importacaoId: string;
}

/** What one request answered, already worded for the screen. `refazer` asks the screen to go back to choosing a file. */
export type Falha = { tipo: "falha"; mensagem: string; refazer?: boolean; semPermissao?: boolean; offline?: boolean };

export const SEM_PERMISSAO = "Você não tem permissão para isso.";
const SEM_CONEXAO = "Sem conexão. Seu arquivo continua aqui; tente de novo quando a internet voltar.";

const MENSAGEM_DO_CODIGO: Record<string, string> = {
  "formato-invalido": "Use um arquivo CSV ou XLSX.",
  "arquivo-invalido": "Não foi possível ler este arquivo. Baixe o modelo e confira as colunas.",
  "arquivo-grande": "O arquivo é grande demais. Divida a planilha e envie por partes.",
  "linhas-demais": "A planilha tem linhas demais. Divida em arquivos menores.",
  "arquivo-com-macro": "A planilha tem macro. Salve uma cópia sem macros (.xlsx ou .csv).",
  "arquivo-com-formula": "A planilha tem fórmula. Copie os valores como texto e envie de novo.",
  "importacao-id-reutilizado": "Este envio já foi usado com outro arquivo. Escolha o arquivo de novo.",
  "cardapio-desatualizado": "O cardápio mudou enquanto você conferia. Escolha o arquivo de novo para refazer a prévia.",
};

export function mensagemDoCodigo(codigo: string): string {
  return MENSAGEM_DO_CODIGO[codigo] ?? "Não foi possível importar. Confira o arquivo e tente de novo.";
}

const n = (v: number | string): number => Number(v);

function paraResumo(r: Esquemas["ResumoDaImportacaoDto"]): Resumo {
  return {
    categoriasCriadas: n(r.categoriasCriadas),
    categoriasAtualizadas: n(r.categoriasAtualizadas),
    produtosCriados: n(r.produtosCriados),
    produtosAtualizados: n(r.produtosAtualizados),
    gruposCriados: n(r.gruposCriados),
    gruposAtualizados: n(r.gruposAtualizados),
    opcoesCriadas: n(r.opcoesCriadas),
    opcoesAtualizadas: n(r.opcoesAtualizadas),
  };
}

function paraPrevia(p: Esquemas["PreviaDaImportacaoDto"]): Previa {
  return {
    valida: p.valida,
    erros: p.erros.map((e) => ({ linha: n(e.linha), coluna: e.coluna, mensagem: e.mensagem })),
    resumo: paraResumo(p.resumo),
  };
}

/** Nothing would be created or changed: the "nothing to change" answer of a repeated import. */
export function semMudanca(r: Resumo): boolean {
  return Object.values(r).every((v) => v === 0);
}

const ROTULOS: [string, keyof Resumo, keyof Resumo][] = [
  ["Categorias", "categoriasCriadas", "categoriasAtualizadas"],
  ["Produtos", "produtosCriados", "produtosAtualizados"],
  ["Grupos de adicionais", "gruposCriados", "gruposAtualizados"],
  ["Opções", "opcoesCriadas", "opcoesAtualizadas"],
];

export function linhasDoResumo(r: Resumo) {
  return ROTULOS.map(([rotulo, c, a]) => ({ rotulo, criados: r[c], atualizados: r[a] }));
}

/** Errors grouped by file line, in line order: one line with two wrong cells is one card. */
export function agruparPorLinha(erros: ErroDeLinha[]): { linha: number; erros: ErroDeLinha[] }[] {
  const mapa = new Map<number, ErroDeLinha[]>();
  for (const e of erros) {
    mapa.set(e.linha, [...(mapa.get(e.linha) ?? []), e]);
  }

  return [...mapa.entries()].sort((a, b) => a[0] - b[0]).map(([linha, lista]) => ({ linha, erros: lista }));
}

export function formatoDoNome(nome: string): "csv" | "xlsx" | null {
  const ext = nome.toLowerCase().split(".").pop();
  return ext === "csv" || ext === "xlsx" ? ext : null;
}

function paraBase64(bytes: Uint8Array): string {
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000) {
    s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }

  return btoa(s);
}

function lerBytes(file: Blob): Promise<ArrayBuffer> {
  return new Promise((resolve, reject) => {
    const leitor = new FileReader();
    leitor.onload = () => resolve(leitor.result as ArrayBuffer);
    leitor.onerror = () => reject(leitor.error);
    leitor.readAsArrayBuffer(file);
  });
}

/** Reads the picked file and mints its `importacaoId`; a refusal sentence (wrong type, too big) comes back instead of a file. */
export async function lerArquivo(file: File, bytesMaximos: number): Promise<Arquivo | string> {
  const formato = formatoDoNome(file.name);
  if (formato === null) {
    return mensagemDoCodigo("formato-invalido");
  }

  if (file.size > bytesMaximos) {
    return mensagemDoCodigo("arquivo-grande");
  }

  const bytes = new Uint8Array(await lerBytes(file));
  return { nome: file.name, formato, conteudoBase64: paraBase64(bytes), importacaoId: uuidv7() };
}

export async function carregarModelo(): Promise<Modelo> {
  const { data, response } = await api.GET("/cardapio/importacao/modelo");
  if (response.status === 403) {
    throw new Error("403");
  }

  if (!data) {
    throw new Error("modelo");
  }

  return { exemploCsv: data.exemploCsv, bytesMaximos: n(data.bytesMaximos), linhasMaximas: n(data.linhasMaximas) };
}

export async function pedirPrevia(a: Arquivo): Promise<{ tipo: "previa"; previa: Previa } | Falha> {
  try {
    const { data, error, response } = await api.POST("/cardapio/importacao/previa", {
      body: { formato: a.formato, conteudoBase64: a.conteudoBase64 },
    });
    if (data) {
      return { tipo: "previa", previa: paraPrevia(data) };
    }

    if (response.status === 403) {
      return { tipo: "falha", mensagem: SEM_PERMISSAO, semPermissao: true };
    }

    if (response.status === 400) {
      const codigo = (error as { codigo?: string } | undefined)?.codigo ?? "";
      return { tipo: "falha", mensagem: mensagemDoCodigo(codigo), refazer: true };
    }

    return { tipo: "falha", mensagem: "Não foi possível conferir o arquivo. Tente de novo." };
  } catch {
    return { tipo: "falha", mensagem: SEM_CONEXAO, offline: true };
  }
}

export type Aplicacao =
  | { tipo: "aplicada"; jaAplicada: boolean; resumo: Resumo }
  | { tipo: "invalida"; previa: Previa }
  | Falha;

export async function aplicar(a: Arquivo): Promise<Aplicacao> {
  try {
    const { data, error, response } = await api.POST("/cardapio/importacao", {
      body: { importacaoId: a.importacaoId, formato: a.formato, conteudoBase64: a.conteudoBase64 },
    });
    if (data && "resumo" in data && "jaAplicada" in data) {
      return { tipo: "aplicada", jaAplicada: data.jaAplicada, resumo: paraResumo(data.resumo) };
    }

    if (response.status === 403) {
      // The preview does not ask the price-change limit, so a valid preview can still be refused here: same bare sentence, never why.
      return { tipo: "falha", mensagem: SEM_PERMISSAO, semPermissao: true };
    }

    if (response.status === 409 || response.status === 400) {
      const codigo = (error as { codigo?: string } | undefined)?.codigo ?? "";
      return { tipo: "falha", mensagem: mensagemDoCodigo(codigo), refazer: true };
    }

    if (response.status === 422) {
      const corpo = error as Esquemas["PreviaDaImportacaoDto"] | undefined;
      if (corpo && "erros" in corpo) {
        return { tipo: "invalida", previa: paraPrevia(corpo) };
      }
    }

    return { tipo: "falha", mensagem: "Não foi possível importar. Tente de novo." };
  } catch {
    return { tipo: "falha", mensagem: SEM_CONEXAO, offline: true };
  }
}
