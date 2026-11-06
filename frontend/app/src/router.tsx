import { createRootRoute, createRoute, createRouter, redirect } from "@tanstack/react-router";
import { Shell } from "./Shell";

function ComandasPlaceholder() {
  return (
    <section className="app-empty" aria-labelledby="salao-base">
      <h2 id="salao-base" className="app-empty-title">
        Base do salão
      </h2>
      <p>Entrar, comandas e caixa chegam nas próximas etapas. Esta tela só confirma que o app instala e abre.</p>
    </section>
  );
}

function CaixaPlaceholder() {
  return (
    <section className="app-empty" aria-labelledby="caixa-base">
      <h2 id="caixa-base" className="app-empty-title">
        Caixa
      </h2>
      <p>O caixa chega numa próxima etapa.</p>
    </section>
  );
}

const rootRoute = createRootRoute({ component: () => <Shell title="Salão" /> });

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/",
  beforeLoad: () => {
    throw redirect({ to: "/salao" });
  },
});

const salaoRoute = createRoute({ getParentRoute: () => rootRoute, path: "/salao", component: ComandasPlaceholder });
const caixaRoute = createRoute({ getParentRoute: () => rootRoute, path: "/salao/caixa", component: CaixaPlaceholder });

const routeTree = rootRoute.addChildren([indexRoute, salaoRoute, caixaRoute]);

export function createAppRouter(history?: Parameters<typeof createRouter>[0]["history"]) {
  return createRouter({ routeTree, ...(history ? { history } : {}) });
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>;
  }
}
