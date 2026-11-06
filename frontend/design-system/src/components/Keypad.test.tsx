import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { Keypad } from "./Keypad";

function setup(props: Partial<React.ComponentProps<typeof Keypad>> = {}) {
  const onDigit = vi.fn();
  const onBackspace = vi.fn();
  const onCancel = vi.fn();
  render(<Keypad onDigit={onDigit} onBackspace={onBackspace} onCancel={onCancel} {...props} />);
  return { onDigit, onBackspace, onCancel };
}

describe("Keypad", () => {
  it("has the twelve keys of a 3x4 pad", () => {
    setup();

    const keys = screen.getAllByRole("button").map((b) => b.textContent);
    expect(keys).toEqual(["1", "2", "3", "4", "5", "6", "7", "8", "9", "Apagar", "0", "Cancelar"]);
  });

  it("reports each digit, backspace and cancel", async () => {
    const { onDigit, onBackspace, onCancel } = setup();

    await userEvent.click(screen.getByRole("button", { name: "7" }));
    await userEvent.click(screen.getByRole("button", { name: "0" }));
    await userEvent.click(screen.getByRole("button", { name: "Apagar" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancelar" }));

    expect(onDigit.mock.calls).toEqual([["7"], ["0"]]);
    expect(onBackspace).toHaveBeenCalledTimes(1);
    expect(onCancel).toHaveBeenCalledTimes(1);
  });

  it("is operable with the keyboard", async () => {
    const { onDigit } = setup();

    await userEvent.tab();
    expect(screen.getByRole("button", { name: "1" })).toHaveFocus();
    await userEvent.keyboard("{Enter}");

    expect(onDigit).toHaveBeenCalledWith("1");
  });

  it("disables the digits while disabled but still lets the user cancel; loading blocks everything", async () => {
    const { onDigit, onCancel } = setup({ disabled: true });
    await userEvent.click(screen.getByRole("button", { name: "5" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancelar" }));
    expect(onDigit).not.toHaveBeenCalled();
    expect(onCancel).toHaveBeenCalledTimes(1);
  });

  it("blocks every key while loading and shows an error message", () => {
    setup({ loading: true, error: "Mesa inexistente." });

    expect(screen.getAllByRole("button").every((b) => (b as HTMLButtonElement).disabled)).toBe(true);
    expect(screen.getByRole("group", { name: "Teclado numérico" })).toHaveAttribute("aria-busy", "true");
    expect(screen.getByRole("alert")).toHaveTextContent("Mesa inexistente.");
  });
});
