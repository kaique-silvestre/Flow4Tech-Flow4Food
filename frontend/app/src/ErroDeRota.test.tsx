import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ErroDeRota, erroDeCarregamento } from "./ErroDeRota";

describe("erroDeCarregamento", () => {
  it.each([
    "Failed to fetch dynamically imported module: https://x/assets/Caixa-1.js",
    "error loading dynamically imported module",
    "Importing a module script failed.",
    "Unable to preload CSS for /assets/x.css",
  ])("recognizes a screen that could not be downloaded: %s", (mensagem) => {
    expect(erroDeCarregamento(new TypeError(mensagem))).toBe(true);
  });

  it("does not mistake a bug for a download failure", () => {
    expect(erroDeCarregamento(new Error("Cannot read properties of undefined"))).toBe(false);
    expect(erroDeCarregamento("texto")).toBe(false);
  });
});

describe("ErroDeRota", () => {
  it("explains the missing connection and retries on request", async () => {
    const reset = vi.fn();
    render(<ErroDeRota error={new TypeError("Failed to fetch dynamically imported module: /a.js")} reset={reset} />);

    expect(screen.getByRole("alert")).toHaveTextContent("Sem conexão para abrir esta tela.");
    await userEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(reset).toHaveBeenCalledOnce();
  });

  it("says something went wrong for any other error, never a blank screen", () => {
    render(<ErroDeRota error={new Error("boom")} reset={() => undefined} />);

    expect(screen.getByRole("alert")).toHaveTextContent("Não foi possível abrir esta tela.");
    expect(screen.queryByText("boom")).not.toBeInTheDocument();
  });
});
