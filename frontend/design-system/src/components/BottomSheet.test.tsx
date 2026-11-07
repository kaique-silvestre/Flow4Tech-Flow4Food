import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { BottomSheet } from "./BottomSheet";
import { Button } from "./Button";

describe("BottomSheet", () => {
  it("is a labelled dialog with title, description, content and footer", () => {
    render(
      <BottomSheet
        open
        onOpenChange={() => undefined}
        title="Enviar rodada"
        description="Vai para a cozinha. Dá para desfazer por 8 segundos."
        footer={<Button>Enviar agora</Button>}
      >
        <p>3 itens</p>
      </BottomSheet>,
    );

    const dialog = screen.getByRole("dialog", { name: "Enviar rodada" });
    expect(dialog).toHaveAccessibleDescription("Vai para a cozinha. Dá para desfazer por 8 segundos.");
    expect(screen.getByText("3 itens")).toBeVisible();
    expect(screen.getByRole("button", { name: "Enviar agora" })).toBeVisible();
  });

  it("renders nothing when closed", () => {
    render(<BottomSheet open={false} onOpenChange={() => undefined} title="Fechar conta" />);

    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("asks to close with Escape and keeps focus inside the sheet", async () => {
    const onOpenChange = vi.fn();
    render(
      <BottomSheet open onOpenChange={onOpenChange} title="Nova comanda" footer={<Button>OK</Button>}>
        <button type="button">Dar um nome</button>
      </BottomSheet>,
    );

    await userEvent.tab();
    expect(screen.getByRole("dialog")).toContainElement(document.activeElement as HTMLElement);
    await userEvent.keyboard("{Escape}");

    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("shows a failed request inline without closing, and announces busy while loading", () => {
    const { rerender } = render(
      <BottomSheet open onOpenChange={() => undefined} title="Fechar conta" error="Sem conexão. Tente de novo." />,
    );
    expect(screen.getByRole("alert")).toHaveTextContent("Sem conexão. Tente de novo.");
    expect(screen.getByRole("dialog")).toBeVisible();

    rerender(<BottomSheet open onOpenChange={() => undefined} title="Fechar conta" loading />);
    expect(screen.getByRole("dialog")).toHaveAttribute("aria-busy", "true");
  });
});
