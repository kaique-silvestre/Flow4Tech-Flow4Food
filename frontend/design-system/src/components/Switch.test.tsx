import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { Switch } from "./Switch";

describe("Switch", () => {
  it("announces its state and asks for the opposite on click", async () => {
    const onCheckedChange = vi.fn();
    const { rerender } = render(<Switch label="Acabou" checked={false} onCheckedChange={onCheckedChange} />);
    const chave = screen.getByRole("switch", { name: "Acabou" });
    expect(chave).toHaveAttribute("aria-checked", "false");
    await userEvent.click(chave);
    expect(onCheckedChange).toHaveBeenCalledWith(true);

    rerender(<Switch label="Acabou" checked onCheckedChange={onCheckedChange} />);
    expect(screen.getByRole("switch", { name: "Acabou" })).toHaveAttribute("aria-checked", "true");
    await userEvent.click(screen.getByRole("switch", { name: "Acabou" }));
    expect(onCheckedChange).toHaveBeenLastCalledWith(false);
  });

  it("is operable with the keyboard", async () => {
    const onCheckedChange = vi.fn();
    render(<Switch label="Ativo" checked onCheckedChange={onCheckedChange} />);
    await userEvent.tab();
    expect(screen.getByRole("switch", { name: "Ativo" })).toHaveFocus();
    await userEvent.keyboard(" ");
    expect(onCheckedChange).toHaveBeenCalledWith(false);
  });

  it("does nothing while loading or disabled, and says it is busy", async () => {
    const onCheckedChange = vi.fn();
    render(<Switch label="Ativo" checked loading onCheckedChange={onCheckedChange} />);
    const chave = screen.getByRole("switch", { name: "Ativo" });
    expect(chave).toHaveAttribute("aria-busy", "true");
    expect(chave).toBeDisabled();
    await userEvent.click(chave);
    expect(onCheckedChange).not.toHaveBeenCalled();
  });
});
