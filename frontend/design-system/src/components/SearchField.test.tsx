import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { SearchField } from "./SearchField";

describe("SearchField", () => {
  it("names the field for assistive tech, with a magnifier and a search keyboard", () => {
    const { container } = render(<SearchField label="Buscar comanda" placeholder="Buscar mesa, balcão ou nome" />);

    const input = screen.getByRole("searchbox", { name: "Buscar comanda" });
    expect(input).toHaveAttribute("inputmode", "search");
    expect(input).toHaveAttribute("placeholder", "Buscar mesa, balcão ou nome");
    expect(container.querySelector(".f4-search-icon svg")).toHaveAttribute("aria-hidden", "true");
  });

  it("reports what is typed", async () => {
    const onChange = vi.fn();
    render(<SearchField label="Buscar comanda" onChange={onChange} />);

    await userEvent.type(screen.getByRole("searchbox"), "ana");

    expect(onChange).toHaveBeenCalledTimes(3);
  });

  it("can be disabled", () => {
    render(<SearchField label="Buscar comanda" disabled />);

    expect(screen.getByRole("searchbox")).toBeDisabled();
  });
});
