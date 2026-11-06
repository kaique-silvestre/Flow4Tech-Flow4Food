import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { Notice, Skeleton } from "./Notice";

describe("Notice", () => {
  it("announces an error at once and says it in words", () => {
    render(
      <Notice tone="error" title="Não deu certo" action={<button type="button">Tentar novamente</button>}>
        Sem conexão.
      </Notice>,
    );

    expect(screen.getByRole("alert")).toHaveTextContent("Não deu certo");
    expect(screen.getByRole("alert")).toHaveTextContent("Sem conexão.");
    expect(screen.getByRole("button", { name: "Tentar novamente" })).toBeVisible();
  });

  it("announces warnings and information politely", () => {
    const { rerender } = render(<Notice tone="warning">Muitas tentativas.</Notice>);
    expect(screen.getByRole("status")).toHaveTextContent("Muitas tentativas.");
    expect(screen.getByRole("status")).toHaveAttribute("data-tone", "warning");

    rerender(<Notice tone="info">Verificação necessária.</Notice>);
    expect(screen.getByRole("status")).toHaveAttribute("data-tone", "info");
  });
});

describe("Skeleton", () => {
  it("shows placeholder rows and says it is loading", () => {
    const { container } = render(<Skeleton rows={4} label="Carregando comandas" />);

    expect(screen.getByRole("status", { name: "Carregando comandas" })).toHaveAttribute("aria-busy", "true");
    expect(container.querySelectorAll(".f4-skeleton")).toHaveLength(4);
  });
});
