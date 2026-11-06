import { RouterProvider } from "@tanstack/react-router";
import { createMemoryHistory } from "@tanstack/react-router";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { createAppRouter } from "./router";

async function renderAt(path: string) {
  const router = createAppRouter(createMemoryHistory({ initialEntries: [path] }));
  render(<RouterProvider router={router} />);
  await router.load();
  return router;
}

describe("salão shell", () => {
  it("sends the root to /salao and renders the shell with the title and both tabs", async () => {
    const router = await renderAt("/");

    expect(await screen.findByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
    expect(router.state.location.pathname).toBe("/salao");
    expect(screen.getByRole("navigation", { name: "Seções do salão" })).toBeVisible();
    expect(screen.getByRole("link", { name: "Comandas" })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("link", { name: "Caixa" })).not.toHaveAttribute("aria-current");
  });

  it("switches sections through the tab bar", async () => {
    const router = await renderAt("/salao");

    await userEvent.click(await screen.findByRole("link", { name: "Caixa" }));

    expect(router.state.location.pathname).toBe("/salao/caixa");
    expect(await screen.findByRole("heading", { level: 2, name: "Caixa" })).toBeVisible();
    expect(screen.getByRole("link", { name: "Caixa" })).toHaveAttribute("aria-current", "page");
  });

  it("keeps the tab links reachable with the keyboard", async () => {
    await renderAt("/salao");

    await userEvent.tab();
    expect(await screen.findByRole("link", { name: "Comandas" })).toHaveFocus();
    await userEvent.tab();
    expect(screen.getByRole("link", { name: "Caixa" })).toHaveFocus();
  });
});
