import { useQueryClient } from "@tanstack/react-query";
import { createRootRoute, createRoute, createRouter, Outlet, redirect } from "@tanstack/react-router";
import { useEffect } from "react";
import { CaixaPage } from "./features/caixa/CaixaPage";
import { CardapioPage } from "./features/cardapio/CardapioPage";
import { ComandaPage } from "./features/comandas/ComandaPage";
import { ComandasPage } from "./features/comandas/ComandasPage";
import { TrocarSenhaPage } from "./features/conta/TrocarSenhaPage";
import { UnidadePage } from "./features/conta/UnidadePage";
import { EntrarPage } from "./features/entrar/EntrarPage";
import { aoEncerrarSessao, destinoDaSessao, lerSessao, restaurar } from "./features/sessao/sessao";
import { PessoasPage } from "./features/usuarios/PessoasPage";
import { EntryShell, Shell, salaoTabs } from "./Shell";

/** When a session ends (button, expired, 401 after refresh) nothing the query cache holds (permissions, bills) stays for the next person on the device. */
function RaizDaAplicacao() {
  const queryClient = useQueryClient();
  useEffect(() => aoEncerrarSessao(() => queryClient.clear()), [queryClient]);
  return <Outlet />;
}

/** The state of the session is known before any screen renders (one refresh attempt through the cookie, shared). */
const rootRoute = createRootRoute({
  beforeLoad: async () => {
    await restaurar();
  },
  component: RaizDaAplicacao,
});

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/",
  beforeLoad: () => {
    throw redirect({ to: "/salao" });
  },
});

/** The tabs of the slice plus Cardápio (who manages products) and Pessoas (who may see profiles): absent, not disabled, for the rest. */
const abas = [
  ...salaoTabs,
  { to: "/salao/cardapio", label: "Cardápio", permissao: "produto:gerenciar" },
  { to: "/salao/pessoas", label: "Pessoas", permissao: "perfil:ver" },
] as const;

/** The app proper: only for a session that is fully ready (password set, unit chosen). Anything else is sent to the screen it still owes. */
const appLayout = createRoute({
  getParentRoute: () => rootRoute,
  id: "app",
  beforeLoad: () => {
    const destino = destinoDaSessao(lerSessao());
    if (destino !== "/salao") {
      throw redirect({ to: destino });
    }
  },
  component: () => <Shell title="Salão" tabs={abas} />,
});

const salaoRoute = createRoute({ getParentRoute: () => appLayout, path: "/salao", component: ComandasPage });
const comandaRoute = createRoute({
  getParentRoute: () => appLayout,
  path: "/salao/comanda/$comandaId",
  component: ComandaPage,
});
const caixaRoute = createRoute({ getParentRoute: () => appLayout, path: "/salao/caixa", component: CaixaPage });

const cardapioRoute = createRoute({
  getParentRoute: () => appLayout,
  path: "/salao/cardapio",
  component: CardapioPage,
});

const pessoasRoute = createRoute({ getParentRoute: () => appLayout, path: "/salao/pessoas", component: PessoasPage });

const entryLayout = createRoute({ getParentRoute: () => rootRoute, id: "entry", component: EntryShell });

function exigir(destino: "/salao/entrar" | "/salao/trocar-senha" | "/salao/unidade") {
  return () => {
    const atual = destinoDaSessao(lerSessao());
    if (atual !== destino) {
      throw redirect({ to: atual });
    }
  };
}

const entrarRoute = createRoute({
  getParentRoute: () => entryLayout,
  path: "/salao/entrar",
  beforeLoad: exigir("/salao/entrar"),
  component: EntrarPage,
});
const trocarSenhaRoute = createRoute({
  getParentRoute: () => entryLayout,
  path: "/salao/trocar-senha",
  beforeLoad: exigir("/salao/trocar-senha"),
  component: TrocarSenhaPage,
});
const unidadeRoute = createRoute({
  getParentRoute: () => entryLayout,
  path: "/salao/unidade",
  beforeLoad: exigir("/salao/unidade"),
  component: UnidadePage,
});

const routeTree = rootRoute.addChildren([
  indexRoute,
  appLayout.addChildren([salaoRoute, comandaRoute, caixaRoute, cardapioRoute, pessoasRoute]),
  entryLayout.addChildren([entrarRoute, trocarSenhaRoute, unidadeRoute]),
]);

export function createAppRouter(history?: Parameters<typeof createRouter>[0]["history"]) {
  return createRouter({ routeTree, ...(history ? { history } : {}) });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
