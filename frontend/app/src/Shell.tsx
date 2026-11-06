import { Link, Outlet } from "@tanstack/react-router";
import type { ReactNode } from "react";

export interface TabItem {
  to: string;
  label: string;
}

/** The slice ships Comandas and Caixa only; each later ticket shows or hides its tab by the caller's permission. */
export const salaoTabs: readonly TabItem[] = [
  { to: "/salao", label: "Comandas" },
  { to: "/salao/caixa", label: "Caixa" },
];

export function Shell({ title, children, tabs = salaoTabs }: { title: string; children?: ReactNode; tabs?: readonly TabItem[] }) {
  return (
    <div className="app-shell">
      <header className="app-topbar">
        <h1 className="f4-title">{title}</h1>
      </header>
      <main className="app-content">{children ?? <Outlet />}</main>
      <nav className="app-tabbar" aria-label="Seções do salão">
        {tabs.map((tab) => (
          <Link key={tab.to} to={tab.to} className="app-tab" activeOptions={{ exact: true }} activeProps={{ "aria-current": "page" }}>
            {tab.label}
          </Link>
        ))}
      </nav>
    </div>
  );
}
