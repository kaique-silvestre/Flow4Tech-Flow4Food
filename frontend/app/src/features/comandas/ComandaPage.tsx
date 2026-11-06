import { Notice, Skeleton, StatusBadge } from "@flow4tech/design-system";
import { Link, useParams } from "@tanstack/react-router";
import { usePermissoes } from "../sessao/permissoes";
import { useComandas } from "./consultas";
import { nomeExibido, tempoDecorrido } from "./tipos";

/** The comanda screen. The launch of items lands here in its own step; for now it identifies the comanda the waiter just opened. */
export function ComandaPage() {
  const { comandaId } = useParams({ from: "/app/salao/comanda/$comandaId" });
  const permissoes = usePermissoes();
  const comandas = useComandas(permissoes.carregado && permissoes.pode("comanda:ver"));
  const comanda = comandas.data?.find((c) => c.id === comandaId);

  if (comandas.isPending) {
    return <Skeleton rows={2} label="Carregando a comanda" />;
  }

  if (!comanda) {
    return (
      <Notice tone="info" action={<Link to="/salao">Voltar às comandas</Link>}>
        Esta comanda não está mais aberta ou não é sua.
      </Notice>
    );
  }

  return (
    <section aria-labelledby="comanda-titulo">
      <p className="comanda-voltar">
        <Link to="/salao">Comandas</Link> › {comanda.numeroTexto}
      </p>
      <h2 id="comanda-titulo" className="app-empty-title">
        {comanda.numeroTexto} · {nomeExibido(comanda)}
      </h2>
      <p className="comandas-vazio">
        <StatusBadge status="aberta" detail={tempoDecorrido(comanda.abertaEm, Date.now())} />
      </p>
      <p className="comandas-vazio">Pronta para lançar itens. O lançamento chega na próxima etapa.</p>
    </section>
  );
}
