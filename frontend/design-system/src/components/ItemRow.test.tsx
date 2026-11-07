import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { formatBRL } from "../format";
import { ItemRow, Stepper } from "./ItemRow";

function renderRow(props: Partial<React.ComponentProps<typeof ItemRow>> = {}) {
  return render(
    <ul>
      <ItemRow quantity={3} name="Chopp 300ml" priceCents={1250} {...props} />
    </ul>,
  );
}

describe("ItemRow", () => {
  it("shows quantity, name, the note and the line total in BRL", () => {
    renderRow({ modifier: "sem colarinho" });

    const row = screen.getByRole("listitem");
    expect(within(row).getByText("3×")).toBeVisible();
    expect(within(row).getByText("sem colarinho")).toBeVisible();
    expect(formatBRL(3750)).toBe("R$ 37,50");
    expect(row.textContent).toContain(formatBRL(3750));
  });

  it("renders the action and fires it", async () => {
    const onCancel = vi.fn();
    renderRow({
      action: (
        <button type="button" className="f4-row-action" onClick={onCancel}>
          Cancelar
        </button>
      ),
    });

    await userEvent.click(screen.getByRole("button", { name: "Cancelar" }));

    expect(onCancel).toHaveBeenCalledTimes(1);
  });

  it("marks a cancelled item and drops its action", () => {
    renderRow({ state: "cancelado", action: <button type="button">Cancelar</button> });

    expect(screen.getByRole("listitem")).toHaveAttribute("data-state", "cancelado");
    expect(screen.getByText("Cancelado")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Cancelar" })).toBeNull();
  });

  it("is busy while its write is in flight", () => {
    renderRow({ loading: true });

    expect(screen.getByRole("listitem")).toHaveAttribute("aria-busy", "true");
    expect(screen.getByRole("listitem")).toHaveAttribute("data-state", "loading");
  });

  it("says what failed and offers the retry", async () => {
    const onRetry = vi.fn();
    renderRow({ error: "Não enviou.", onRetry });

    expect(screen.getByRole("alert")).toHaveTextContent("Não enviou.");
    expect(screen.getByRole("listitem")).toHaveAttribute("data-state", "error");
    await userEvent.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(onRetry).toHaveBeenCalledTimes(1);
  });
});

function Example({ min, max }: { min?: number; max?: number }) {
  const [value, setValue] = useState(2);
  return (
    <Stepper
      label="Chopp"
      value={value}
      onChange={setValue}
      {...(min === undefined ? {} : { min })}
      {...(max === undefined ? {} : { max })}
    />
  );
}

describe("Stepper", () => {
  it("raises and lowers the quantity", async () => {
    render(<Example />);

    await userEvent.click(screen.getByRole("button", { name: "Aumentar Chopp" }));
    await userEvent.click(screen.getByRole("button", { name: "Aumentar Chopp" }));
    await userEvent.click(screen.getByRole("button", { name: "Diminuir Chopp" }));

    expect(screen.getByRole("group", { name: "Chopp" })).toHaveTextContent("3");
  });

  it("stops at the limits", async () => {
    render(<Example min={2} max={3} />);

    expect(screen.getByRole("button", { name: "Diminuir Chopp" })).toBeDisabled();
    await userEvent.click(screen.getByRole("button", { name: "Aumentar Chopp" }));
    expect(screen.getByRole("button", { name: "Aumentar Chopp" })).toBeDisabled();
  });
});
