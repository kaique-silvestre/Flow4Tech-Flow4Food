import { readFileSync } from "node:fs";
import { join } from "node:path";
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { StatusBadge, type ComandaStatus } from "./StatusBadge";

describe("StatusBadge", () => {
  it.each<[ComandaStatus, string]>([
    ["aberta", "Aberta"],
    ["aguardando-pagamento", "Aguardando pagamento"],
    ["parada", "Parada"],
    ["fechada", "Fechada"],
  ])("says %s in words, never by color alone", (status, word) => {
    const { container } = render(<StatusBadge status={status} />);

    expect(screen.getByText(word)).toBeVisible();
    expect(container.querySelector(".f4-badge")).toHaveAttribute("data-status", status);
    expect(container.querySelector(".f4-badge-marker")).toHaveAttribute("aria-hidden", "true");
  });

  it("adds the elapsed time after the word", () => {
    render(<StatusBadge status="parada" detail="há 1h 12min" />);

    expect(screen.getByText("Parada há 1h 12min")).toBeVisible();
  });

  it("gives every status its own marker shape in the stylesheet", () => {
    const css = readFileSync(join(import.meta.dirname, "..", "styles.css"), "utf8");

    expect(css).toMatch(/data-status="aberta"\] \.f4-badge-marker \{[^}]*border-radius: 50%/s);
    expect(css).toMatch(/data-status="aguardando-pagamento"\] \.f4-badge-marker \{[^}]*clip-path: polygon\(50% 0, 100% 100%, 0 100%\)/s);
    expect(css).toMatch(/data-status="parada"\] \.f4-badge-marker \{[^}]*clip-path: polygon\(50% 0, 100% 50%, 50% 100%, 0 50%\)/s);
    expect(css).toMatch(/data-status="fechada"\] \.f4-badge-marker \{[^}]*border-radius: 2px/s);
  });
});
