import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it, vi } from "vitest";
import { QueueList, type AcaoDaFila } from "./QueueList";

const acoes: AcaoDaFila[] = [
  { id: "a", titulo: "Rodada · #7", detalhe: "3 itens · R$ 59,90", estado: "enviando", situacao: "Enviando" },
  { id: "b", titulo: "Rodada · #8", detalhe: "1 item · R$ 12,50", estado: "pendente", situacao: "Aguardando conexão" },
  { id: "c", titulo: "Rodada · #9", detalhe: "2 itens · R$ 14,00", estado: "falhou", situacao: "Não enviada: item indisponível" },
];

describe("QueueList", () => {
  it("lists the actions oldest first, each with its state in words", () => {
    render(<QueueList acoes={acoes} onTentar={() => undefined} onRemover={() => undefined} />);

    const itens = within(screen.getByRole("list", { name: "Ações pendentes" })).getAllByRole("listitem");
    expect(itens.map((i) => i.getAttribute("data-estado"))).toEqual(["enviando", "pendente", "falhou"]);
    expect(itens[1]).toHaveTextContent("Rodada · #8");
    expect(itens[1]).toHaveTextContent("Aguardando conexão");
    expect(itens[2]).toHaveTextContent("Não enviada: item indisponível");
  });

  it("offers Tentar agora only for a pending action, and Remover for any that is not in flight", async () => {
    const onTentar = vi.fn();
    const onRemover = vi.fn();
    render(<QueueList acoes={acoes} onTentar={onTentar} onRemover={onRemover} />);

    expect(screen.getAllByRole("button", { name: "Tentar agora" })).toHaveLength(1);
    expect(screen.queryByRole("button", { name: "Remover Rodada · #7" })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Tentar agora" }));
    await userEvent.click(screen.getByRole("button", { name: "Remover Rodada · #9" }));

    expect(onTentar).toHaveBeenCalledWith("b");
    expect(onRemover).toHaveBeenCalledWith("c");
  });

  it("gives each state its own marker shape and stills the sending pulse under reduced motion", () => {
    const css = readFileSync(join(import.meta.dirname, "..", "styles.css"), "utf8");

    expect(css).toMatch(/data-estado="pendente"\] \.f4-queue-marcador \{[^}]*clip-path: polygon\(50% 0, 100% 100%, 0 100%\)/s);
    expect(css).toMatch(/data-estado="falhou"\] \.f4-queue-marcador \{[^}]*clip-path: polygon\(50% 0, 100% 50%, 50% 100%, 0 50%\)/s);
    expect(css).toMatch(/data-estado="enviando"\] \.f4-queue-marcador \{[^}]*border-radius: 50%/s);
    expect(css).toMatch(/prefers-reduced-motion: reduce\) \{\s*\.f4-queue-item\[data-estado="enviando"\] \.f4-queue-marcador \{\s*animation: none/s);
  });
});
