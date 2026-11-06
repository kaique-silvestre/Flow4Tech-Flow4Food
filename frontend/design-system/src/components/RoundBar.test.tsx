import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { afterEach, describe, expect, it, vi } from "vitest";
import { formatBRL } from "../format";
import { RoundBar } from "./RoundBar";
import { UndoToast } from "./UndoToast";

afterEach(() => {
  vi.useRealTimers();
});

describe("RoundBar", () => {
  it("does not exist until something is staged", () => {
    const { container } = render(<RoundBar itens={0} totalCentavos={0} action={<button type="button">Enviar</button>} />);

    expect(container).toBeEmptyDOMElement();
  });

  it("says how many units are not sent, the value, that nothing reaches the kitchen yet, and offers the action", async () => {
    const onSend = vi.fn();
    render(
      <RoundBar
        itens={5}
        totalCentavos={9650}
        action={
          <button type="button" onClick={onSend}>
            Enviar
          </button>
        }
      />,
    );

    const barra = screen.getByRole("region", { name: "Rodada não enviada" });
    expect(barra).toHaveTextContent("5 itens não enviados");
    expect(barra.textContent).toContain(formatBRL(9650));
    expect(barra).toHaveTextContent("Cozinha e bar só recebem ao enviar");
    await userEvent.click(screen.getByRole("button", { name: "Enviar" }));
    expect(onSend).toHaveBeenCalledTimes(1);
  });

  it("uses the singular for one unit", () => {
    render(<RoundBar itens={1} totalCentavos={1250} action={<button type="button">Enviar</button>} />);

    expect(screen.getByRole("region")).toHaveTextContent("1 item não enviado");
  });
});

describe("UndoToast", () => {
  it("offers the undo with the seconds left in words and runs a linear bar for the window", () => {
    const { container } = render(<UndoToast message="Rodada enviada" durationMs={8000} onUndo={() => undefined} onExpire={() => undefined} />);

    expect(screen.getByRole("status")).toHaveTextContent("Rodada enviada (8 s)");
    expect(screen.getByRole("button", { name: "Desfazer" })).toBeEnabled();
    const bar = container.querySelector<HTMLElement>(".f4-toast-bar");
    expect(bar?.style.animationDuration).toBe("8000ms");
    const css = readFileSync(join(import.meta.dirname, "..", "styles.css"), "utf8");
    expect(css).toMatch(/\.f4-toast-bar \{[^}]*animation-timing-function: linear/s);
    expect(css).toMatch(/prefers-reduced-motion: reduce\) \{\s*\.f4-toast-bar \{\s*animation: none/s);
  });

  it("undoes when asked and counts down; when the window ends the action is gone and the owner is told", async () => {
    vi.useFakeTimers();
    const onUndo = vi.fn();
    const onExpire = vi.fn();
    render(<UndoToast message="Rodada enviada" durationMs={8000} onUndo={onUndo} onExpire={onExpire} />);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3000);
    });
    expect(screen.getByRole("status")).toHaveTextContent("(5 s)");
    screen.getByRole("button", { name: "Desfazer" }).click();
    expect(onUndo).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(5200);
    });
    expect(onExpire).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("button", { name: "Desfazer" })).toBeDisabled();
  });

  it("blocks a second press while the undo request is in flight", () => {
    render(<UndoToast message="Rodada enviada" durationMs={8000} onUndo={() => undefined} onExpire={() => undefined} loading />);

    expect(screen.getByRole("button", { name: "Desfazer" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Desfazer" })).toHaveAttribute("aria-busy", "true");
  });
});
