import { Button } from "@flow4tech/design-system";
import { Link, Outlet, useNavigate } from "@tanstack/react-router";
import { type ReactNode, useEffect } from "react";
import { BarraDeFila } from "./features/fila/BarraDeFila";
import { useMapaDeMesas } from "./features/mesas/consultas";
import { semMesas } from "./features/mesas/tipos";
import { usePermissoes } from "./features/sessao/permissoes";
import { sair, useSessao } from "./features/sessao/sessao";

export interface TabItem {
  to: string;
  label: string;
  /** The tab is absent (not disabled) for a caller who lacks it. */
  permissao: string;
  /** Absent too when the unit has no tables to show (it does not use them, or none is registered). */
  requerMesas?: boolean;
}

/** The slice ships Comandas and Caixa only; each later step shows or hides its tab by the caller's permission. */
export const salaoTabs: readonly TabItem[] = [
  { to: "/salao", label: "Comandas", permissao: "comanda:ver" },
  { to: "/salao/mesas", label: "Mesas", permissao: "mesa:ver", requerMesas: true },
  { to: "/salao/caixa", label: "Caixa", permissao: "caixa:ver" },
];

/** Leaves the screen when the session ends (signed out here, expired, or revoked): the person lands on the sign-in. */
function useSairQuandoAnonima() {
  const sessao = useSessao();
  const navigate = useNavigate();
  useEffect(() => {
    if (sessao.status === "anonima") {
      void navigate({ to: "/salao/entrar" });
    }
  }, [sessao.status, navigate]);
}

export function Shell({
  title,
  children,
  tabs = salaoTabs,
}: {
  title: string;
  children?: ReactNode;
  tabs?: readonly TabItem[];
}) {
  const navigate = useNavigate();
  useSairQuandoAnonima();
  const permissoes = usePermissoes();
  const mapa = useMapaDeMesas(permissoes.pode("mesa:ver") && tabs.some((tab) => tab.requerMesas));
  const temMesas = mapa.data !== undefined && !semMesas(mapa.data);
  const visiveis = tabs.filter((tab) => permissoes.pode(tab.permissao) && (!tab.requerMesas || temMesas));
  return (
    <div className="app-shell">
      <header className="app-topbar">
        <h1 className="f4-title">{title}</h1>
        <Button
          variant="secondary"
          className="app-signout"
          onClick={() => {
            // Ending the session forgets what was waiting (the session hooks count it and tell the sign-in screen): the next person on the device never sends it.
            void sair().then(() => navigate({ to: "/salao/entrar" }));
          }}
        >
          Sair
        </Button>
      </header>
      <BarraDeFila />
      <main className="app-content">{children ?? <Outlet />}</main>
      <nav className="app-tabbar" aria-label="Seções do salão" hidden={visiveis.length === 0}>
        {visiveis.map((tab) => (
          <Link
            key={tab.to}
            to={tab.to}
            className="app-tab"
            activeOptions={{ exact: true }}
            activeProps={{ "aria-current": "page" }}
          >
            {tab.label}
          </Link>
        ))}
      </nav>
    </div>
  );
}

/** Sign-in, password change and unit choice: one centered column, no tab bar (nothing else is reachable yet). */
export function EntryShell() {
  const sessao = useSessao();
  const navigate = useNavigate();
  useEffect(() => {
    if (sessao.status === "anonima" && window.location.pathname !== "/salao/entrar") {
      void navigate({ to: "/salao/entrar" });
    }
  }, [sessao.status, navigate]);
  return (
    <div className="entry-shell">
      <p className="entry-brand">Flow4Tech Salão</p>
      <main className="entry-main">
        <Outlet />
      </main>
    </div>
  );
}
