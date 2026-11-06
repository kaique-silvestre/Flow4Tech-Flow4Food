import { Button, Notice, Skeleton, StatusBadge } from "@flow4tech/design-system";
import { useQuery } from "@tanstack/react-query";
import { api } from "../sessao/sessao";

/** The landing of /salao. The list itself (open comandas, numbers, filters) is built in its own step; this shows the real states: loading, empty, failed, and the rows. */
export function ComandasPage() {
  const comandas = useQuery({
    queryKey: ["comandas"],
    queryFn: async () => {
      const { data } = await api.GET("/comandas");
      if (!data) {
        throw new Error("comandas");
      }

      return data;
    },
  });

  if (comandas.isPending) {
    return <Skeleton rows={4} label="Carregando comandas" />;
  }

  if (comandas.isError) {
    return (
      <Notice
        tone="error"
        action={
          <Button variant="secondary" onClick={() => void comandas.refetch()}>
            Tentar novamente
          </Button>
        }
      >
        Não foi possível carregar as comandas.
      </Notice>
    );
  }

  if (comandas.data.length === 0) {
    return (
      <section className="app-empty" aria-labelledby="sem-comandas">
        <h2 id="sem-comandas" className="app-empty-title">
          Nenhuma comanda aberta
        </h2>
        <p>Quando houver comandas abertas, elas aparecem aqui.</p>
      </section>
    );
  }

  return (
    <ul className="comanda-list" aria-label="Comandas abertas">
      {comandas.data.map((comanda) => (
        <li key={comanda.id} className="comanda-row">
          <span className="f4-num comanda-numero">{comanda.numeroTexto}</span>
          <span className="comanda-nome">{comanda.identificacao}</span>
          <StatusBadge status="aberta" />
        </li>
      ))}
    </ul>
  );
}
