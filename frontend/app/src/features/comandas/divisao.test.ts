import { describe, expect, it } from "vitest";
import {
  centesimosDoPercentual,
  digitarPercentual,
  formularioInicial,
  mudarPartes,
  pedidoDaDivisao,
  textoDaFalhaDaDivisao,
} from "./divisao";
import { alternarSelecao, textoDoMovimento } from "./movimento";
import type { ItemDaConta } from "./rodada";

const item = (id: string, centavos = 1000): ItemDaConta => ({
  id,
  rodadaId: "r",
  produtoId: "p",
  produtoNome: id,
  precoCentavos: centavos,
  quantidade: 1,
  observacao: null,
  estado: "lancado",
  opcoes: [],
  unitarioCentavos: centavos,
});
const itens = [item("a"), item("b"), item("c")];

describe("split form", () => {
  it("starts on equal parts with two parts and builds the request without a network call", () => {
    const f = formularioInicial(itens, 2750);
    expect(pedidoDaDivisao(f, itens, 2750)).toEqual({
      ok: true,
      pedido: { modo: "iguais", partes: 2, grupos: null, valores: null, percentuais: null, gorjetaCentavos: 0 },
    });
  });

  it("changes the number of parts within 2 to 50", () => {
    const f = formularioInicial(itens, 2750);
    expect(mudarPartes(f, 3, itens, 2750).partes).toBe(3);
    expect(mudarPartes(f, 1, itens, 2750).partes).toBe(2);
    expect(mudarPartes(f, 51, itens, 2750).partes).toBe(50);
  });

  it("seat mode sends only the mode", () => {
    const f = { ...formularioInicial(itens, 2750), modo: "assento" as const };
    expect(pedidoDaDivisao(f, itens, 2750)).toMatchObject({ ok: true, pedido: { modo: "assento", partes: null } });
  });

  it("item mode spreads the items over the parts and sends the groups; an empty part is refused locally", () => {
    const f = { ...formularioInicial(itens, 2750), modo: "item" as const };
    const r = pedidoDaDivisao(f, itens, 2750);
    expect(r).toMatchObject({ ok: true, pedido: { modo: "item", grupos: [["a", "c"], ["b"]] } });
    const vazio = { ...f, partes: 3, grupoPorItem: { a: 1, b: 1, c: 1 } };
    expect(pedidoDaDivisao(vazio, itens, 2750)).toMatchObject({
      ok: false,
      aviso: expect.stringContaining("pelo menos um item"),
    });
  });

  it("changing the parts keeps each item on a part that still exists", () => {
    const f = {
      ...formularioInicial(itens, 2750),
      modo: "item" as const,
      partes: 3,
      grupoPorItem: { a: 3, b: 2, c: 1 },
    };
    expect(mudarPartes(f, 2, itens, 2750).grupoPorItem).toEqual({ a: 1, b: 2, c: 1 });
  });

  it("free values start as an even split that already adds up, and must add up to the bill", () => {
    const f = { ...formularioInicial(itens, 2751), modo: "valores" as const };
    const dois = mudarPartes(f, 2, itens, 2751);
    expect(dois.valores).toEqual(["1376", "1375"]);
    expect(pedidoDaDivisao(dois, itens, 2751)).toMatchObject({ ok: true, pedido: { valores: [1376, 1375] } });
    const errado = { ...dois, valores: ["1000", "1375"] };
    expect(pedidoDaDivisao(errado, itens, 2751)).toMatchObject({ ok: false, aviso: expect.stringContaining("Faltam") });
    const passou = { ...dois, valores: ["2000", "1375"] };
    expect(pedidoDaDivisao(passou, itens, 2751)).toMatchObject({ ok: false, aviso: expect.stringContaining("Passou") });
  });

  it("percents start even (33,34 / 33,33 / 33,33) and must add up to 100", () => {
    const f = mudarPartes({ ...formularioInicial(itens, 2750), modo: "percentual" as const }, 3, itens, 2750);
    expect(f.percentuais).toEqual(["33.34", "33.33", "33.33"]);
    expect(pedidoDaDivisao(f, itens, 2750)).toMatchObject({ ok: true, pedido: { percentuais: [33.34, 33.33, 33.33] } });
    expect(pedidoDaDivisao({ ...f, percentuais: ["50", "40", "5"] }, itens, 2750)).toMatchObject({
      ok: false,
      aviso: expect.stringContaining("100"),
    });
  });

  it("reads a percent with comma or dot and at most two decimals", () => {
    expect(centesimosDoPercentual("33,5")).toBe(3350);
    expect(centesimosDoPercentual("12.34")).toBe(1234);
    expect(centesimosDoPercentual("")).toBe(0);
    expect(centesimosDoPercentual("abc")).toBeNull();
    expect(centesimosDoPercentual("101")).toBeNull();
    expect(digitarPercentual("12,345")).toBe("12,34");
    expect(digitarPercentual("1a2")).toBe("12");
    expect(digitarPercentual("1,2,3")).toBe("1,23");
  });
});

describe("selection", () => {
  it("toggles an id in and out without touching the others", () => {
    const um = alternarSelecao(new Set(["a"]), "b");
    expect([...um]).toEqual(["a", "b"]);
    expect([...alternarSelecao(um, "a")]).toEqual(["b"]);
  });
});

describe("sentences", () => {
  it("names each refusal of a movement and never mentions a limit", () => {
    expect(textoDoMovimento("transferir", 409, "item-com-desconto")).toContain("desconto");
    expect(textoDoMovimento("juntar", 409, "comanda-com-desconto")).toContain("desconto");
    expect(textoDoMovimento("juntar", 409, "valor-ja-recebido")).toContain("pagamento");
    expect(textoDoMovimento("transferir", 409, "comanda-nao-aberta")).toContain("aberta");
    expect(textoDoMovimento("transferir", 409, "versao-desatualizada")).toContain("outro aparelho");
    expect(textoDoMovimento("transferir", 403, undefined)).toBe("Você não tem permissão para transferir itens.");
    expect(textoDoMovimento("separar", 500, undefined)).toContain("Tente de novo");
  });

  it("names each refusal of a split", () => {
    expect(textoDaFalhaDaDivisao(400, "invalido", "assento")).toContain("assento");
    expect(textoDaFalhaDaDivisao(400, "soma-diferente", "valores")).toContain("somar");
    expect(textoDaFalhaDaDivisao(400, "itens-fora-das-partes", "item")).toContain("item");
    expect(textoDaFalhaDaDivisao(409, "valor-ja-recebido", "iguais")).toContain("recebeu");
    expect(textoDaFalhaDaDivisao(409, "comanda-nao-aberta", "iguais")).toContain("aberta");
    expect(textoDaFalhaDaDivisao(409, "versao-desatualizada", "iguais")).toContain("outro aparelho");
    expect(textoDaFalhaDaDivisao(403, undefined, "iguais")).toBe("Você não tem permissão para dividir a conta.");
  });
});
