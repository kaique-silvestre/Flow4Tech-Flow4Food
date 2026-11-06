import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { json, renderEm, sessaoJson, stubRede } from "./test-utils";

afterEach(() => {
  vi.unstubAllGlobals();
});

const pronta = { "POST /refresh": json(sessaoJson()), "GET /comandas": json([]), "GET /caixa/turnos/atual": new Response(null, { status: 404 }) };

describe("salão shell (signed in)", () => {
  it("sends the root to /salao and renders the title and both tabs", async () => {
    stubRede(pronta);

    const { router } = await renderEm("/");

    expect(await screen.findByRole("heading", { level: 1, name: "Salão" })).toBeVisible();
    expect(router.state.location.pathname).toBe("/salao");
    expect(await screen.findByRole("navigation", { name: "Seções do salão" })).toBeVisible();
    expect(screen.getByRole("link", { name: "Comandas" })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("link", { name: "Caixa" })).not.toHaveAttribute("aria-current");
  });

  it("switches sections through the tab bar", async () => {
    stubRede(pronta);
    const { router } = await renderEm("/salao");

    await userEvent.click(await screen.findByRole("link", { name: "Caixa" }));

    expect(router.state.location.pathname).toBe("/salao/caixa");
    expect(await screen.findByText("Caixa fechado. Abra o caixa para receber.")).toBeVisible();
    expect(screen.getByRole("link", { name: "Caixa" })).toHaveAttribute("aria-current", "page");
  });

  it("keeps the tab links reachable with the keyboard", async () => {
    stubRede(pronta);
    await renderEm("/salao");
    await screen.findByRole("heading", { level: 1, name: "Salão" });

    await userEvent.tab();
    expect(screen.getByRole("button", { name: "Sair" })).toHaveFocus();
    await userEvent.tab();
    expect(await screen.findByRole("button", { name: "Nova comanda" })).toHaveFocus();
    await userEvent.tab();
    expect(await screen.findByRole("link", { name: "Comandas" })).toHaveFocus();
    await userEvent.tab();
    expect(screen.getByRole("link", { name: "Caixa" })).toHaveFocus();
  });
});
