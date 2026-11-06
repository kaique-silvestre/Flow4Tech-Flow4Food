import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { Tab, TabList, TabPanel, Tabs } from "./Tabs";

function Example({ loading = false, error = false }: { loading?: boolean; error?: boolean }) {
  return (
    <Tabs defaultValue="frequentes">
      <TabList aria-label="Categorias">
        <Tab value="frequentes">Frequentes</Tab>
        <Tab value="bebidas" loading={loading} error={error}>
          Bebidas
        </Tab>
        <Tab value="porcoes" disabled>
          Porções
        </Tab>
      </TabList>
      <TabPanel value="frequentes">Itens frequentes</TabPanel>
      <TabPanel value="bebidas">Itens de bebida</TabPanel>
    </Tabs>
  );
}

describe("Tabs", () => {
  it("shows the active panel and switches on click", async () => {
    render(<Example />);
    expect(screen.getByRole("tab", { name: "Frequentes" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByText("Itens frequentes")).toBeVisible();

    await userEvent.click(screen.getByRole("tab", { name: "Bebidas" }));

    expect(screen.getByRole("tab", { name: "Bebidas" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByText("Itens de bebida")).toBeVisible();
  });

  it("moves between tabs with the arrow keys and skips a disabled one", async () => {
    render(<Example />);

    await userEvent.tab();
    expect(screen.getByRole("tab", { name: "Frequentes" })).toHaveFocus();
    await userEvent.keyboard("{ArrowRight}");
    expect(screen.getByRole("tab", { name: "Bebidas" })).toHaveFocus();
    await userEvent.keyboard("{ArrowRight}");

    expect(screen.getByRole("tab", { name: "Frequentes" })).toHaveFocus();
  });

  it("does not select a disabled tab", async () => {
    render(<Example />);

    await userEvent.click(screen.getByRole("tab", { name: "Porções" }));

    expect(screen.getByRole("tab", { name: "Porções" })).toBeDisabled();
    expect(screen.getByRole("tab", { name: "Frequentes" })).toHaveAttribute("aria-selected", "true");
  });

  it("marks a loading tab as busy and an errored one", () => {
    const { rerender } = render(<Example loading />);
    expect(screen.getByRole("tab", { name: "Bebidas" })).toHaveAttribute("aria-busy", "true");
    expect(screen.getByRole("tab", { name: "Bebidas" })).toBeDisabled();

    rerender(<Example error />);
    expect(screen.getByRole("tab", { name: "Bebidas" })).toHaveAttribute("data-error", "true");
  });
});
