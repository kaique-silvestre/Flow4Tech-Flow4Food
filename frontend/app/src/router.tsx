import { useQueryClient } from "@tanstack/react-query";
import {
  createRootRoute,
  createRoute,
  createRouter,
  lazyRouteComponent,
  Outlet,
  redirect,
  useRouter,
} from "@tanstack/react-router";
import { useEffect } from "react";
import { ErroDeRota } from "./ErroDeRota";
import { ComandasPage } from "./features/comandas/ComandasPage";
import { aoEncerrarSessao, destinoDaSessao, lerSessao, restaurar } from "./features/sessao/sessao";
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
  component: lazyRouteComponent(() => import("./features/comandas/ComandaPage"), "ComandaPage"),
});
const caixaRoute = createRoute({
  getParentRoute: () => appLayout,
  path: "/salao/caixa",
  component: lazyRouteComponent(() => import("./features/caixa/CaixaPage"), "CaixaPage"),
});
const mesasRoute = createRoute({
  getParentRoute: () => appLayout,
  path: "/salao/mesas",
  component: lazyRouteComponent(() => import("./features/mesas/MapaDeMesasPage"), "MapaDeMesasPage"),
});

const cardapioRoute = createRoute({
  getParentRoute: () => appLayout,
  path: "/salao/cardapio",
  component: lazyRouteComponent(() => import("./features/cardapio/CardapioPage"), "CardapioPage"),
});

const pessoasRoute = createRoute({
  getParentRoute: () => appLayout,
  path: "/salao/pessoas",
  component: lazyRouteComponent(() => import("./features/usuarios/PessoasPage"), "PessoasPage"),
});

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
  component: lazyRouteComponent(() => import("./features/entrar/EntrarPage"), "EntrarPage"),
});
const trocarSenhaRoute = createRoute({
  getParentRoute: () => entryLayout,
  path: "/salao/trocar-senha",
  beforeLoad: exigir("/salao/trocar-senha"),
  component: lazyRouteComponent(() => import("./features/conta/TrocarSenhaPage"), "TrocarSenhaPage"),
});
const unidadeRoute = createRoute({
  getParentRoute: () => entryLayout,
  path: "/salao/unidade",
  beforeLoad: exigir("/salao/unidade"),
  component: lazyRouteComponent(() => import("./features/conta/UnidadePage"), "UnidadePage"),
});

const routeTree = rootRoute.addChildren([
  indexRoute,
  appLayout.addChildren([salaoRoute, comandaRoute, mesasRoute, caixaRoute, cardapioRoute, pessoasRoute]),
  entryLayout.addChildren([entrarRoute, trocarSenhaRoute, unidadeRoute]),
]);

/** Retrying a screen whose code did not download re-runs the route load (which downloads it again); resetting the boundary alone would show the same error. */
function ErroDaTela({ error, reset }: { error: unknown; reset: () => void }) {
  const router = useRouter();
  return (
    <ErroDeRota
      error={error}
      reset={() => {
        reset();
        void router.invalidate();
      }}
    />
  );
}

export function createAppRouter(history?: Parameters<typeof createRouter>[0]["history"]) {
  return createRouter({
    routeTree,
    // The screens a person opens on a bad network are downloaded when the link is pressed or about to be (hover, touch): the comanda one is also warmed from the list (ComandasPage).
    defaultPreload: "intent",
    defaultErrorComponent: ErroDaTela,
    ...(history ? { history } : {}),
  });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
