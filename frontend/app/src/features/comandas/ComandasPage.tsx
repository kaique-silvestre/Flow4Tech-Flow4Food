import { Button, Notice, Skeleton, StatusBadge } from "@flow4tech/design-system";
import { Link } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { useOnline } from "../fila/conexao";
import { usePermissoes } from "../sessao/permissoes";
import { useComandas, useMesas } from "./consultas";
import { NovaComandaSheet } from "./NovaComandaSheet";
import { combinaBusca, nomeExibido, tempoDecorrido } from "./tipos";

function useAgora(intervaloMs = 30_000) {
  const [agora, setAgora] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setAgora(Date.now()), intervaloMs);
    return () => window.clearInterval(timer);
  }, [intervaloMs]);
  return agora;
}

/** The comanda screen is the waiter's main path and a separate download: it is fetched as soon as the list is on screen (the service worker keeps it for the next time without signal), so opening a comanda waits for nothing. Failing to fetch it is not an error here: opening it will say so. */
function useAquecerComanda() {
  useEffect(() => {
    const timer = window.setTimeout(() => void import("./ComandaPage").catch(() => undefined), 0);
    return () => window.clearTimeout(timer);
  }, []);
}

/** The landing of /salao: the open comandas of the waiter (all of the unit for who may see others'), with search, elapsed time and one accent action: Nova comanda. */
export function ComandasPage() {
  const permissoes = usePermissoes();
  const pode = permissoes.carregado && permissoes.pode("comanda:ver");
  const comandas = useComandas(pode);
  const mesas = useMesas(pode && permissoes.pode("comanda:abrir"));
  const agora = useAgora();
  useAquecerComanda();
  const online = useOnline();
  const [busca, setBusca] = useState("");
  const [nova, setNova] = useState(false);

  if (permissoes.carregado && !permissoes.pode("comanda:ver")) {
    return <Notice tone="info">Você não tem permissão para ver as comandas.</Notice>;
  }

  const verTodas = permissoes.pode("comanda:ver-comandas-de-outros");
  const lista = (comandas.data ?? []).filter((c) => combinaBusca(c, busca));

  return (
    <section className="comandas" aria-labelledby="comandas-titulo">
      <h2 id="comandas-titulo" className="app-empty-title">
        {verTodas ? "Comandas abertas" : "Minhas comandas"}
      </h2>
      {!online ? <Notice tone="info">Sem conexão. A lista pode estar desatualizada.</Notice> : null}

      {comandas.isPending ? <Skeleton rows={4} label="Carregando comandas" /> : null}

      {comandas.isError && comandas.data === undefined ? (
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
      ) : null}

      {comandas.isSuccess && comandas.data.length === 0 ? (
        <p className="comandas-vazio">Nenhuma comanda aberta. Toque em Nova comanda.</p>
      ) : null}

      {comandas.isSuccess && comandas.data.length > 0 ? (
        <>
          <div className="f4-field comandas-busca">
            <label htmlFor="busca-comandas" className="f4-visually-hidden">
              Buscar comanda
            </label>
            <input
              id="busca-comandas"
              className="f4-input"
              type="search"
              inputMode="search"
              placeholder="Buscar por número ou nome"
              autoComplete="off"
              value={busca}
              onChange={(e) => setBusca(e.target.value)}
            />
          </div>
          {lista.length === 0 ? (
            <p className="comandas-vazio">Nada com “{busca}”. Tente o número da comanda.</p>
          ) : (
            <ul className="comanda-list" aria-label="Comandas abertas">
              {lista.map((comanda) => (
                <li key={comanda.id}>
                  <Link to="/salao/comanda/$comandaId" params={{ comandaId: comanda.id }} className="comanda-row">
                    <span className="f4-num comanda-numero">{comanda.numeroTexto}</span>
                    <span className="comanda-nome">
                      {nomeExibido(comanda)}
                      <span className="comanda-tempo">{tempoDecorrido(comanda.abertaEm, agora)}</span>
                    </span>
                    <StatusBadge status="aberta" />
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </>
      ) : null}

      {permissoes.pode("comanda:abrir") ? (
        <div className="comandas-acao">
          <Button className="entry-submit" onClick={() => setNova(true)}>
            Nova comanda
          </Button>
        </div>
      ) : null}

      <NovaComandaSheet open={nova} onOpenChange={setNova} mesas={mesas.data ?? []} comandas={comandas.data ?? []} />
    </section>
  );
}
