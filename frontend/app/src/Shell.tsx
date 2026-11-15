import { Button, Icon, type IconName } from "@flow4tech/design-system";
import { Link, Outlet, useNavigate } from "@tanstack/react-router";
import { type ReactNode, useEffect } from "react";
import { BarraDeFila } from "./features/fila/BarraDeFila";
import { useMapaDeMesas } from "./features/mesas/consultas";
import { semMesas } from "./features/mesas/tipos";
import { useContextoDaSessao } from "./features/sessao/contexto";
import { usePermissoes } from "./features/sessao/permissoes";
import { sair, useSessao } from "./features/sessao/sessao";
import { useTema } from "./features/sessao/tema";

export interface TabItem {
  to: string;
  label: string;
  /** Drawn above the label; the label stays (an icon alone is never the only sign). */
  icone: IconName;
  /** The tab is absent (not disabled) for a caller who lacks it. */
  permissao: string;
  /** Absent too when the unit has no tables to show (it does not use them, or none is registered). */
  requerMesas?: boolean;
}

/** The slice ships Comandas and Caixa only; each later step shows or hides its tab by the caller's permission. */
export const salaoTabs: readonly TabItem[] = [
  { to: "/salao", label: "Comandas", icone: "list", permissao: "comanda:ver" },
  { to: "/salao/mesas", label: "Mesas", icone: "grid", permissao: "mesa:ver", requerMesas: true },
  { to: "/salao/caixa", label: "Caixa", icone: "wallet", permissao: "caixa:ver" },
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
  const contexto = useContextoDaSessao();
  const { tema, alternar } = useTema();
  const permissoes = usePermissoes();
  const mapa = useMapaDeMesas(permissoes.pode("mesa:ver") && tabs.some((tab) => tab.requerMesas));
  const temMesas = mapa.data !== undefined && !semMesas(mapa.data);
  const visiveis = tabs.filter((tab) => permissoes.pode(tab.permissao) && (!tab.requerMesas || temMesas));
  return (
    <div className="app-shell">
      <header className="app-topbar">
        <h1 className="f4-title app-title">{title}</h1>
        <div className="app-topbar-acoes">
          <button
            type="button"
            className="app-tema"
            onClick={alternar}
            aria-label={tema === "escuro" ? "Mudar para o tema claro" : "Mudar para o tema escuro"}
          >
            <Icon name={tema === "escuro" ? "sun" : "moon"} />
          </button>
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
        </div>
        {contexto ? (
          <p className="app-contexto">
            {contexto.organizacao} · {contexto.unidade} · <strong>{contexto.usuario}</strong>
          </p>
        ) : null}
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
            <Icon name={tab.icone} />
            <span>{tab.label}</span>
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
