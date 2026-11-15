import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { Icon, type IconName } from "./Icon";

const names: IconName[] = [
  "search",
  "list",
  "grid",
  "wallet",
  "book",
  "users",
  "plus",
  "sun",
  "moon",
  "warn",
  "clock",
  "dot",
  "check",
  "logout",
];

describe("Icon", () => {
  it.each(names)("draws %s as a decorative svg", (name) => {
    const { container } = render(<Icon name={name} />);
    const svg = container.querySelector("svg");

    expect(svg).toHaveAttribute("aria-hidden", "true");
    expect(svg?.querySelectorAll("path").length).toBeGreaterThan(0);
  });

  it("takes its size from the caller and its color from the text", () => {
    const { container } = render(<Icon name="plus" size={32} />);

    expect(container.querySelector("svg")).toHaveAttribute("width", "32");
    expect(container.querySelector("svg")).toHaveAttribute("stroke", "currentColor");
  });
});
