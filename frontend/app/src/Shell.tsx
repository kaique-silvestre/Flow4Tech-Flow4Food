import {
  Button,
  type EscolhaDoMenu,
  type Faixa,
  Icon,
  type IconName,
  menuRecolhido,
  useFaixa,
} from "@flow4tech/design-system";
import { Link, Outlet, useNavigate } from "@tanstack/react-router";
import { type ReactNode, useEffect, useState } from "react";
import { useLeituraDoCaixa } from "./features/caixa/dados";
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

/** The tabs this caller may see: the engine's answer (`pode`) decides, a tab they lack is absent, never disabled. Same list on every band: the band only changes where it is drawn. */
export function abasVisiveis(
  tabs: readonly TabItem[],
  pode: (permissao: string) => boolean,
  temMesas: boolean,
): readonly TabItem[] {
  return tabs.filter((tab) => pode(tab.permissao) && (!tab.requerMesas || temMesas));
}

const CHAVE_DO_MENU = "f4t-menu";

function escolhaSalvaDoMenu(): EscolhaDoMenu {
  try {
    const valor = window.localStorage.getItem(CHAVE_DO_MENU);
    return valor === "trilho" || valor === "completo" ? valor : null;
  } catch {
    return null;
  }
}

/** The person's own choice of rail or full menu, per device and never sent to the server; it beats the automatic one of the band. */
function useEscolhaDoMenu() {
  const [escolha, definir] = useState<EscolhaDoMenu>(escolhaSalvaDoMenu);
  const guardar = (nova: Exclude<EscolhaDoMenu, null>) => {
    definir(nova);
    try {
      window.localStorage.setItem(CHAVE_DO_MENU, nova);
    } catch {
      // Private mode: the choice lasts until the page closes.
    }
  };
  return [escolha, guardar] as const;
}

const horaLocal = new Intl.DateTimeFormat("pt-BR", { hour: "2-digit", minute: "2-digit" });

/** The shift of the caller's cash register, read from the server like the rest of the header; absent while unknown (the header only mounts it for callers with `caixa:ver`). */
function TurnoDoCabecalho() {
  const leitura = useLeituraDoCaixa();
  const dado = leitura.data;
  if (dado === undefined) {
    return null;
  }

  const texto =
    dado.tipo === "turno"
      ? `Turno aberto desde ${horaLocal.format(new Date(dado.resumo.turno.abertoEm))}`
      : "Sem turno aberto";
  return (
    <>
      {" · "}
      <span className="app-turno">{texto}</span>
    </>
  );
}

function Navegacao({ tabs, faixa, recolhido }: { tabs: readonly TabItem[]; faixa: Faixa; recolhido: boolean }) {
  return (
    <nav
      className={faixa === "celular" ? "app-tabbar" : "app-lateral-nav"}
      aria-label="Seções do salão"
      data-menu={faixa === "celular" ? "inferior" : recolhido ? "trilho" : "completo"}
      hidden={tabs.length === 0}
    >
      {tabs.map((tab) => (
        <Link
          key={tab.to}
          to={tab.to}
          className="app-tab"
          title={recolhido ? tab.label : undefined}
          aria-label={recolhido ? tab.label : undefined}
          activeOptions={{ exact: true }}
          activeProps={{ "aria-current": "page" }}
        >
          <Icon name={tab.icone} />
          <span className="app-tab-rotulo">{tab.label}</span>
        </Link>
      ))}
    </nav>
  );
}

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
  const visiveis = abasVisiveis(tabs, permissoes.pode, temMesas);
  const faixa = useFaixa();
  const [escolha, guardarEscolha] = useEscolhaDoMenu();
  const recolhido = menuRecolhido(faixa, escolha);
  const comMenuLateral = faixa !== "celular";
  return (
    <div
      className="app-shell"
      data-faixa={faixa}
      data-menu={comMenuLateral ? (recolhido ? "trilho" : "completo") : "inferior"}
    >
      {comMenuLateral ? (
        <aside className="app-lateral">
          <p className="app-marca">
            <span className="app-marca-sinal">F4</span>
            <span className="app-marca-nome">Flow4Tech Salão</span>
          </p>
          <Navegacao tabs={visiveis} faixa={faixa} recolhido={recolhido} />
          <button
            type="button"
            className="app-recolher"
            aria-expanded={!recolhido}
            aria-label={recolhido ? "Expandir o menu" : "Recolher o menu"}
            title={recolhido ? "Expandir o menu" : "Recolher o menu"}
            onClick={() => guardarEscolha(recolhido ? "completo" : "trilho")}
          >
            <Icon name="chevron" />
            <span className="app-tab-rotulo">Recolher menu</span>
          </button>
        </aside>
      ) : null}
      <div className="app-coluna">
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
              {contexto.organizacao} · {contexto.unidade}
              {permissoes.pode("caixa:ver") ? <TurnoDoCabecalho /> : null} · <strong>{contexto.usuario}</strong>
            </p>
          ) : null}
        </header>
        <BarraDeFila />
        <main className="app-content">{children ?? <Outlet />}</main>
        {comMenuLateral ? null : <Navegacao tabs={visiveis} faixa={faixa} recolhido={false} />}
      </div>
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
