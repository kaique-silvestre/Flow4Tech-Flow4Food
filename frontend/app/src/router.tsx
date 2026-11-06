import { createRootRoute, createRoute, createRouter, Outlet, redirect } from "@tanstack/react-router";
import { CaixaPage } from "./features/caixa/CaixaPage";
import { ComandasPage } from "./features/comandas/ComandasPage";
import { TrocarSenhaPage } from "./features/conta/TrocarSenhaPage";
import { UnidadePage } from "./features/conta/UnidadePage";
import { EntrarPage } from "./features/entrar/EntrarPage";
import { destinoDaSessao, lerSessao, restaurar } from "./features/sessao/sessao";
import { EntryShell, Shell } from "./Shell";

/** The state of the session is known before any screen renders (one refresh attempt through the cookie, shared). */
const rootRoute = createRootRoute({
  beforeLoad: async () => {
    await restaurar();
  },
  component: Outlet,
});

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/",
  beforeLoad: () => {
    throw redirect({ to: "/salao" });
  },
});

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
  component: () => <Shell title="Salão" />,
});

const salaoRoute = createRoute({ getParentRoute: () => appLayout, path: "/salao", component: ComandasPage });
const caixaRoute = createRoute({ getParentRoute: () => appLayout, path: "/salao/caixa", component: CaixaPage });

const entryLayout = createRoute({ getParentRoute: () => rootRoute, id: "entry", component: EntryShell });

function exigir(destino: "/salao/entrar" | "/salao/trocar-senha" | "/salao/unidade") {
  return () => {
    const atual = destinoDaSessao(lerSessao());
    if (atual !== destino) {
      throw redirect({ to: atual });
    }
  };
}

const entrarRoute = createRoute({ getParentRoute: () => entryLayout, path: "/salao/entrar", beforeLoad: exigir("/salao/entrar"), component: EntrarPage });
const trocarSenhaRoute = createRoute({ getParentRoute: () => entryLayout, path: "/salao/trocar-senha", beforeLoad: exigir("/salao/trocar-senha"), component: TrocarSenhaPage });
const unidadeRoute = createRoute({ getParentRoute: () => entryLayout, path: "/salao/unidade", beforeLoad: exigir("/salao/unidade"), component: UnidadePage });

const routeTree = rootRoute.addChildren([indexRoute, appLayout.addChildren([salaoRoute, caixaRoute]), entryLayout.addChildren([entrarRoute, trocarSenhaRoute, unidadeRoute])]);

export function createAppRouter(history?: Parameters<typeof createRouter>[0]["history"]) {
  return createRouter({ routeTree, ...(history ? { history } : {}) });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
