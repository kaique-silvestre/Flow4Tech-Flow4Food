import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { formatBRL } from "../format";
import { MoneyField } from "./MoneyField";
import { ToneBadge } from "./ToneBadge";

function Example({ initial = "" }: { initial?: string }) {
  const [digits, setDigits] = useState(initial);
  return (
    <>
      <MoneyField label="Fundo" digits={digits} onDigitsChange={setDigits} />
      <output aria-label="digits">{digits}</output>
    </>
  );
}

describe("MoneyField", () => {
  it("shows nothing until something is typed, then fills the cents from the right", async () => {
    render(<Example />);
    const input = screen.getByLabelText("Fundo");
    expect(input).toHaveValue("");
    expect(input).toHaveAttribute("placeholder", formatBRL(0));

    await userEvent.type(input, "1250");

    expect(input).toHaveValue(formatBRL(1250));
    expect(screen.getByLabelText("digits")).toHaveTextContent("1250");
  });

  it("keeps a typed zero as a real value and drops letters and extra leading zeros", async () => {
    render(<Example />);
    const input = screen.getByLabelText("Fundo");

    await userEvent.type(input, "0");
    expect(input).toHaveValue(formatBRL(0));
    expect(screen.getByLabelText("digits")).toHaveTextContent("0");

    await userEvent.clear(input);
    await userEvent.type(input, "a0b05");
    expect(screen.getByLabelText("digits")).toHaveTextContent("5");
  });

  it("opens the numeric keypad and stops at a sane length", async () => {
    render(<Example />);
    const input = screen.getByLabelText("Fundo");
    expect(input).toHaveAttribute("inputmode", "numeric");

    await userEvent.type(input, "12345678901234");

    expect(screen.getByLabelText("digits")).toHaveTextContent("123456789");
  });
});

describe("ToneBadge", () => {
  it.each([
    ["success", "aberta"],
    ["warning", "aguardando-pagamento"],
    ["alert", "parada"],
    ["neutral", "fechada"],
  ] as const)("says the word with the %s marker", (tone, status) => {
    const { container } = render(<ToneBadge tone={tone}>Falta R$ 5,00</ToneBadge>);

    expect(screen.getByText("Falta R$ 5,00")).toBeVisible();
    expect(container.querySelector(".f4-badge")).toHaveAttribute("data-status", status);
    expect(container.querySelector(".f4-badge-marker")).toHaveAttribute("aria-hidden", "true");
  });
});
