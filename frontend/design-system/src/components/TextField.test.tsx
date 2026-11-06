import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { TextField } from "./TextField";

describe("TextField", () => {
  it("labels the input and accepts typing", async () => {
    const onChange = vi.fn();
    render(<TextField label="Usuário" onChange={onChange} />);

    await userEvent.type(screen.getByLabelText("Usuário"), "ana");

    expect(onChange).toHaveBeenCalledTimes(3);
    expect(screen.getByLabelText("Usuário")).toHaveValue("ana");
  });

  it("links the hint and the error to the input and marks it invalid", () => {
    render(<TextField label="Senha" hint="Pelo menos 10 caracteres." error="Muito curta." />);

    const input = screen.getByLabelText("Senha");
    expect(input).toHaveAttribute("aria-invalid", "true");
    expect(input).toHaveAccessibleDescription("Muito curta. Pelo menos 10 caracteres.");
    expect(screen.getByRole("alert")).toHaveTextContent("Muito curta.");
  });

  it("is reachable with the keyboard and keeps its trailing action at its own tab stop", async () => {
    render(<TextField label="Senha" trailing={<button type="button">Mostrar</button>} />);

    await userEvent.tab();
    expect(screen.getByLabelText("Senha")).toHaveFocus();
    await userEvent.tab();
    expect(screen.getByRole("button", { name: "Mostrar" })).toHaveFocus();
  });

  it("is blocked and busy while loading, and ignores typing when disabled", async () => {
    const { rerender } = render(<TextField label="Usuário" loading />);
    expect(screen.getByLabelText("Usuário")).toBeDisabled();
    expect(screen.getByLabelText("Usuário")).toHaveAttribute("aria-busy", "true");

    rerender(<TextField label="Usuário" disabled />);
    await userEvent.type(screen.getByLabelText("Usuário"), "x");
    expect(screen.getByLabelText("Usuário")).toHaveValue("");
  });
});
