import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { Button } from "./Button";

describe("Button", () => {
  it("renders the primary variant by default and fires on click", async () => {
    const onClick = vi.fn();
    render(<Button onClick={onClick}>Enviar</Button>);

    const button = screen.getByRole("button", { name: "Enviar" });
    expect(button).toHaveAttribute("data-variant", "primary");
    expect(button).toHaveAttribute("data-state", "default");
    await userEvent.click(button);

    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it("renders the secondary variant", () => {
    render(<Button variant="secondary">Voltar</Button>);

    expect(screen.getByRole("button", { name: "Voltar" })).toHaveAttribute("data-variant", "secondary");
  });

  it("is reachable and activated with the keyboard (focus-visible target)", async () => {
    const onClick = vi.fn();
    render(<Button onClick={onClick}>OK</Button>);

    await userEvent.tab();
    expect(screen.getByRole("button", { name: "OK" })).toHaveFocus();
    await userEvent.keyboard("{Enter}");

    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it("ignores clicks while disabled", async () => {
    const onClick = vi.fn();
    render(
      <Button disabled onClick={onClick}>
        Enviar
      </Button>,
    );

    await userEvent.click(screen.getByRole("button", { name: "Enviar" }));

    expect(screen.getByRole("button", { name: "Enviar" })).toBeDisabled();
    expect(onClick).not.toHaveBeenCalled();
  });

  it("shows a spinner and ignores clicks while loading, announcing it as busy", async () => {
    const onClick = vi.fn();
    const { container } = render(
      <Button loading onClick={onClick}>
        Enviar
      </Button>,
    );

    const button = screen.getByRole("button", { name: "Enviar" });
    await userEvent.click(button);

    expect(button).toHaveAttribute("aria-busy", "true");
    expect(button).toHaveAttribute("data-state", "loading");
    expect(container.querySelector(".f4-spinner")).not.toBeNull();
    expect(onClick).not.toHaveBeenCalled();
  });

  it("shows the error inline, linked to the button, and stays usable to retry", async () => {
    const onClick = vi.fn();
    render(
      <Button error="Não foi possível enviar." onClick={onClick}>
        Enviar
      </Button>,
    );

    const button = screen.getByRole("button", { name: "Enviar" });
    expect(screen.getByRole("alert")).toHaveTextContent("Não foi possível enviar.");
    expect(button).toHaveAttribute("data-state", "error");
    expect(button).toHaveAccessibleDescription("Não foi possível enviar.");
    await userEvent.click(button);

    expect(onClick).toHaveBeenCalledTimes(1);
  });
});
