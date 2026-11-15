import { readFileSync } from "node:fs";
import { join } from "node:path";
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { type ComandaStatus, type MesaStatus, StatusBadge } from "./StatusBadge";

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

  it.each<[MesaStatus, string]>([
    ["livre", "Livre"],
    ["ocupada", "Ocupada"],
    ["aguardando-conta", "Aguardando conta"],
  ])("says the table status %s in words too", (status, word) => {
    const { container } = render(<StatusBadge status={status} />);

    expect(screen.getByText(word)).toBeVisible();
    expect(container.querySelector(".f4-badge")).toHaveAttribute("data-status", status);
  });

  it("adds the elapsed time after the word", () => {
    render(<StatusBadge status="parada" detail="há 1h 12min" />);

    expect(screen.getByText("Parada há 1h 12min")).toBeVisible();
  });

  it("gives every status its own marker shape in the stylesheet", () => {
    const css = readFileSync(join(import.meta.dirname, "..", "styles.css"), "utf8");

    expect(css).toMatch(/data-status="aberta"\] \.f4-badge-marker[^{]*\{[^}]*border-radius: 50%/s);
    expect(css).toMatch(
      /data-status="aguardando-pagamento"\] \.f4-badge-marker[^{]*\{[^}]*clip-path: polygon\(50% 0, 100% 100%, 0 100%\)/s,
    );
    expect(css).toMatch(
      /data-status="parada"\] \.f4-badge-marker \{[^}]*clip-path: polygon\(50% 0, 100% 50%, 50% 100%, 0 50%\)/s,
    );
    expect(css).toMatch(/data-status="fechada"\] \.f4-badge-marker \{[^}]*border-radius: 2px/s);
    expect(css).toMatch(
      /data-status="livre"\] \.f4-badge-marker \{[^}]*background: transparent;[^}]*box-shadow: inset/s,
    );
    expect(css).toMatch(/data-status="ocupada"\] \.f4-badge-marker[^{]*\{[^}]*border-radius: 50%/s);
    expect(css).toMatch(
      /data-status="aguardando-conta"\] \.f4-badge-marker[^{]*\{[^}]*clip-path: polygon\(50% 0, 100% 100%, 0 100%\)/s,
    );
  });

  it("draws an icon instead of the shaped marker when asked, keeping the word and the elapsed time", () => {
    const { container } = render(<StatusBadge status="parada" detail="há 52 min" icon />);

    expect(screen.getByText("Parada há 52 min")).toBeVisible();
    expect(container.querySelector(".f4-badge-marker")).toBeNull();
    expect(container.querySelector("svg.f4-icon")).toHaveAttribute("aria-hidden", "true");
  });
});
