import type { components } from "@flow4tech/api-client";

type ProdutoDto = components["schemas"]["ProdutoDto"];
type CategoriaDto = components["schemas"]["CategoriaDto"];
type AlterarProdutoRequest = components["schemas"]["AlterarProdutoRequest"];

interface Promocao {
  precoCentavos: number;
  /** ISO instant (UTC). The promotion holds in [inicio, fim). */
  inicio: string;
  fim: string;
}

export interface ProdutoGestao {
  id: string;
  categoriaId: string;
  codigo: string;
  nome: string;
  precoCentavos: number;
  /** What a waiter charges here and now: unit override > promotion in force > base. */
  precoEfetivoCentavos: number;
  precoDaUnidadeCentavos: number | null;
  ativo: boolean;
  indisponivel: boolean;
  versao: number;
  promocao: Promocao | null;
}

export interface CategoriaGestao {
  id: string;
  nome: string;
  ativa: boolean;
  categoriaPaiId: string | null;
}

/** The generated client types int64 as number-or-string; the screen works with numbers (cents stay far below 2^53). */
export function paraProdutoGestao(dto: ProdutoDto): ProdutoGestao {
  return {
    id: dto.id,
    categoriaId: dto.categoriaId,
    codigo: dto.codigo,
    nome: dto.nome,
    precoCentavos: Number(dto.precoCentavos),
    precoEfetivoCentavos: Number(dto.precoEfetivoCentavos),
    precoDaUnidadeCentavos: dto.precoDaUnidadeCentavos === null ? null : Number(dto.precoDaUnidadeCentavos),
    ativo: dto.ativo,
    indisponivel: dto.indisponivel,
    versao: Number(dto.versao),
    promocao: dto.promocao
      ? { precoCentavos: Number(dto.promocao.precoCentavos), inicio: dto.promocao.inicio, fim: dto.promocao.fim }
      : null,
  };
}

export function paraCategoriaGestao(dto: CategoriaDto): CategoriaGestao {
  return { id: dto.id, nome: dto.nome, ativa: dto.ativa, categoriaPaiId: dto.categoriaPaiId };
}

export interface Grupo {
  id: string;
  nome: string;
  /** 0 = category, 1 = subcategory (the model allows two levels at most). */
  nivel: 0 | 1;
  produtos: ProdutoGestao[];
}

const porNome = (a: { nome: string }, b: { nome: string }) => a.nome.localeCompare(b.nome, "pt-BR");

/** Category, then its subcategories, each with its products by name. A category with nothing to show (no product of its own or of a child) is left out. */
export function agrupar(produtos: readonly ProdutoGestao[], categorias: readonly CategoriaGestao[]): Grupo[] {
  const conhecidas = new Set(categorias.map((c) => c.id));
  const de = (id: string) => produtos.filter((p) => p.categoriaId === id).sort(porNome);
  const grupos: Grupo[] = [];
  for (const pai of categorias.filter((c) => c.categoriaPaiId === null).sort(porNome)) {
    const filhas = categorias.filter((c) => c.categoriaPaiId === pai.id).sort(porNome);
    const proprios = de(pai.id);
    const filhasComProduto = filhas.filter((f) => de(f.id).length > 0);
    if (proprios.length === 0 && filhasComProduto.length === 0) {
      continue;
    }

    grupos.push({ id: pai.id, nome: pai.nome, nivel: 0, produtos: proprios });
    for (const filha of filhasComProduto) {
      grupos.push({ id: filha.id, nome: filha.nome, nivel: 1, produtos: de(filha.id) });
    }
  }

  const orfaos = produtos.filter((p) => !conhecidas.has(p.categoriaId)).sort(porNome);
  if (orfaos.length > 0) {
    grupos.push({ id: "sem-categoria", nome: "Sem categoria", nivel: 0, produtos: orfaos });
  }

  return grupos;
}

function normalizar(texto: string): string {
  return texto
    .normalize("NFD")
    .replace(/\p{Diacritic}/gu, "")
    .toLowerCase();
}

/** Part of the name or of the short code, ignoring case and accents (same rule as the waiter's picker). */
export function filtrar(produtos: readonly ProdutoGestao[], busca: string): ProdutoGestao[] {
  const termo = normalizar(busca.trim());
  if (termo === "") {
    return [...produtos];
  }

  return produtos.filter((p) => normalizar(p.nome).includes(termo) || normalizar(p.codigo).includes(termo));
}

export interface CamposDePreco {
  /** Digits as typed in the money field (cents). */
  base: string;
  promo: string;
  /** datetime-local values. */
  inicio: string;
  fim: string;
}

export type ErrosDePreco = Partial<Record<"base" | "promo" | "inicio" | "fim", string>>;

const centavos = (digitos: string) => (digitos === "" ? 0 : Number(digitos));

/** What the sheet can tell before sending; the server still has the last word (it answers 400 for the same rules and 403 for the right to change a price). */
export function validarPreco(campos: CamposDePreco): ErrosDePreco {
  const erros: ErrosDePreco = {};
  const base = centavos(campos.base);
  if (base <= 0) {
    erros.base = "Informe um preço maior que zero.";
  }

  if (campos.promo === "" || centavos(campos.promo) === 0) {
    return erros;
  }

  if (centavos(campos.promo) >= base) {
    erros.promo = "O preço promocional precisa ser menor que o preço normal.";
  }

  if (campos.inicio === "") {
    erros.inicio = "Informe quando a promoção começa.";
  }

  if (campos.fim === "") {
    erros.fim = "Informe quando a promoção termina.";
  } else if (campos.inicio !== "" && campos.fim <= campos.inicio) {
    erros.fim = "O fim precisa ser depois do início.";
  }

  return erros;
}

const doisDigitos = (n: number) => String(n).padStart(2, "0");

/** ISO instant -> the value a datetime-local input shows, in the device's time zone. */
export function paraInputLocal(iso: string): string {
  const d = new Date(iso);
  return `${d.getFullYear()}-${doisDigitos(d.getMonth() + 1)}-${doisDigitos(d.getDate())}T${doisDigitos(d.getHours())}:${doisDigitos(d.getMinutes())}`;
}

/** The value of a datetime-local input (device time zone) -> ISO instant in UTC, which is what the server stores. */
export function deInputLocal(valor: string): string {
  return new Date(valor).toISOString();
}

/** Types the date and time as digits and lays the separators in as it goes: "101120261030" -> "10/11/2026 10:30". Native date-time inputs follow the browser locale (en-US shows mm/dd/yyyy), so the screen uses a text field in the format the staff reads. */
export function mascararDataHora(entrada: string): string {
  const d = entrada.replace(/\D/g, "").slice(0, 12);
  const partes = [d.slice(0, 2), d.slice(2, 4), d.slice(4, 8)].filter(Boolean).join("/");
  const hora = [d.slice(8, 10), d.slice(10, 12)].filter(Boolean).join(":");
  return hora ? `${partes} ${hora}` : partes;
}

/** "dd/mm/aaaa hh:mm" -> "aaaa-mm-ddThh:mm" (the form the rest of the logic uses); "" stays ""; null when it is not a real date and time. */
export function lerDataHoraBR(texto: string): string | null {
  if (texto.trim() === "") {
    return "";
  }

  const m = /^(\d{2})\/(\d{2})\/(\d{4}) (\d{2}):(\d{2})$/.exec(texto.trim());
  if (!m) {
    return null;
  }

  const [, dia, mes, ano, hora, minuto] = m.map(Number) as [number, number, number, number, number, number];
  const d = new Date(ano, mes - 1, dia, hora, minuto);
  const real =
    d.getFullYear() === ano &&
    d.getMonth() === mes - 1 &&
    d.getDate() === dia &&
    d.getHours() === hora &&
    d.getMinutes() === minuto;
  return real ? `${ano}-${doisDigitos(mes)}-${doisDigitos(dia)}T${doisDigitos(hora)}:${doisDigitos(minuto)}` : null;
}

/** "aaaa-mm-ddThh:mm" -> "dd/mm/aaaa hh:mm". */
export function paraTextoBR(local: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(local);
  return m ? `${m[3]}/${m[2]}/${m[1]} ${m[4]}:${m[5]}` : "";
}

export interface Mudanca {
  ativo?: boolean;
  indisponivel?: boolean;
  precoCentavos?: number;
  /** undefined keeps the current promotion; null removes it. */
  promocao?: Promocao | null;
}

/** The whole product as the update route wants it (it replaces, it does not patch), with the version the screen saw and only the asked change applied. */
export function corpoDaAlteracao(produto: ProdutoGestao, mudanca: Mudanca): AlterarProdutoRequest {
  return {
    codigo: produto.codigo,
    nome: produto.nome,
    precoCentavos: mudanca.precoCentavos ?? produto.precoCentavos,
    categoriaId: produto.categoriaId,
    ativo: mudanca.ativo ?? produto.ativo,
    indisponivel: mudanca.indisponivel ?? produto.indisponivel,
    versao: produto.versao,
    promocao: mudanca.promocao === undefined ? produto.promocao : mudanca.promocao,
  };
}
