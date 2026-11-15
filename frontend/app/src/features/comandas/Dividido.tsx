import { useFaixa } from "@flow4tech/design-system";
import { Outlet } from "@tanstack/react-router";
import { ComandasPage } from "./ComandasPage";

/** Parent of /salao and /salao/comanda/:id. Tablet and computer (ADR-019): the list on the left, whatever the child route shows in a 420 px side panel (the prototype's list + detail split, `.sal-split`); the list is mounted once, so its search and filter survive opening a comanda. Reference: .scratch/S-00-novo-sistema/prototipos/index.html#m=web&open=3. Phone: the child alone. */
export function SalaoLayout() {
  return useFaixa() !== "celular" ? (
    <div className="dividido">
      <ComandasPage />
      <section className="dividido-painel" aria-label="Comanda aberta">
        <Outlet />
      </section>
    </div>
  ) : (
    <Outlet />
  );
}

/** The route of /salao: the phone list, or on the tablet and computer (the list is the layout's) the panel asking for a comanda. */
export function SalaoRota() {
  return useFaixa() !== "celular" ? (
    <p className="comandas-vazio">Escolha uma comanda para ver a conta.</p>
  ) : (
    <ComandasPage />
  );
}
