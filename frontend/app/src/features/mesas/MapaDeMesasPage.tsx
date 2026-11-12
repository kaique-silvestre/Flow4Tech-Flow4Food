import { Button, Notice, Skeleton, StatusBadge } from "@flow4tech/design-system";
import { useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { useComandas } from "../comandas/consultas";
import { paraComanda } from "../comandas/tipos";
import { useOnline } from "../fila/conexao";
import { usePermissoes } from "../sessao/permissoes";
import { api } from "../sessao/sessao";
import { useMapaDeMesas } from "./consultas";
import { FolhaDaMesa } from "./FolhaDaMesa";
import { TrocarMesaSheet } from "./TrocarMesaSheet";
import { detalheDaMesa, type MesaDoMapa, nomeDaMesa, semMesas, todasAsMesas } from "./tipos";
import "./mesas.css";

function useAgora(intervaloMs = 30_000) {
  const [agora, setAgora] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setAgora(Date.now()), intervaloMs);
    return () => window.clearInterval(timer);
  }, [intervaloMs]);
  return agora;
}

/**
 * The salon by area. A free table opens a comanda on it with one tap; an occupied one lists its comandas (open one, move one to another table, open another). Every status carries its
 * word, never only a color. Actions the caller may not do are absent, not disabled: the server still decides each one.
 */
export function MapaDeMesasPage() {
  const permissoes = usePermissoes();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const podeVer = permissoes.carregado && permissoes.pode("mesa:ver");
  const podeAbrir = permissoes.pode("comanda:abrir");
  const podeVerComandas = permissoes.pode("comanda:ver");
  const podeTrocar = permissoes.pode("comanda:transferir-mesa") && podeVerComandas;
  const mapa = useMapaDeMesas(podeVer);
  const comandas = useComandas(podeVerComandas);
  const agora = useAgora();
  const online = useOnline();
  const [folha, setFolha] = useState<string | null>(null);
  const [trocando, setTrocando] = useState<string | null>(null);
  const [abrindo, setAbrindo] = useState<string | null>(null);
  const [erro, setErro] = useState<string | undefined>(undefined);
  const [aviso, setAviso] = useState<string | undefined>(undefined);

  if (permissoes.carregado && !permissoes.pode("mesa:ver")) {
    return <Notice tone="info">Você não tem permissão para ver as mesas.</Notice>;
  }

  const mesas = mapa.data ? todasAsMesas(mapa.data) : [];
  const mesaDaFolha = mesas.find((m) => m.id === folha) ?? null;
  const comandaParaTrocar = (comandas.data ?? []).find((c) => c.id === trocando) ?? null;

  async function abrirNaMesa(mesa: MesaDoMapa) {
    setErro(undefined);
    setAviso(undefined);
    setAbrindo(mesa.id);
    try {
      const { data, response } = await api.POST("/comandas", {
        body: { identificacao: null, mesaId: mesa.id, pessoas: null },
      });
      if (data) {
        const comanda = paraComanda(data as unknown as Record<string, unknown>);
        await Promise.all([
          queryClient.invalidateQueries({ queryKey: ["comandas"] }),
          queryClient.invalidateQueries({ queryKey: ["mesas"] }),
        ]);
        setFolha(null);
        await navigate({ to: "/salao/comanda/$comandaId", params: { comandaId: comanda.id } });
      } else if (response.status === 403) {
        setErro("Você não tem permissão para abrir comandas.");
      } else if (response.status === 409) {
        setErro("Esta unidade não usa mesas. Atualizamos a tela.");
        await queryClient.invalidateQueries({ queryKey: ["mesas"] });
      } else {
        setErro("Não foi possível abrir a comanda. Atualizamos a tela, tente de novo.");
        await queryClient.invalidateQueries({ queryKey: ["mesas"] });
      }
    } catch {
      setErro("Sem conexão. Tente de novo quando o sinal voltar.");
    } finally {
      setAbrindo(null);
    }
  }

  function linha(mesa: MesaDoMapa) {
    const detalhe = detalheDaMesa(mesa, agora);
    const conteudo = (
      <>
        <span className="f4-num mesa-nome">{nomeDaMesa(mesa)}</span>
        {detalhe !== "" ? <span className="mesa-detalhe">{detalhe}</span> : null}
        <StatusBadge status={mesa.status} />
      </>
    );
    const livre = mesa.status === "livre";
    if (livre && podeAbrir) {
      return (
        <button
          type="button"
          className="mesa-row"
          disabled={abrindo !== null}
          aria-label={`${nomeDaMesa(mesa)}, livre. Abrir comanda`}
          onClick={() => void abrirNaMesa(mesa)}
        >
          {conteudo}
        </button>
      );
    }

    if (!livre && podeVerComandas) {
      return (
        <button
          type="button"
          className="mesa-row"
          aria-label={`${nomeDaMesa(mesa)}, ${mesa.status === "ocupada" ? "ocupada" : "aguardando conta"}${detalhe === "" ? "" : `, ${detalhe}`}. Ver comandas`}
          onClick={() => setFolha(mesa.id)}
        >
          {conteudo}
        </button>
      );
    }

    return <div className="mesa-row">{conteudo}</div>;
  }

  function grupo(id: string, titulo: string, lista: readonly MesaDoMapa[]) {
    return (
      <section key={id} aria-labelledby={`mesas-${id}`}>
        <h3 id={`mesas-${id}`} className="mesas-area">
          {titulo}
          <span className="mesas-area-conta">
            {lista.length} {lista.length === 1 ? "mesa" : "mesas"}
          </span>
        </h3>
        {lista.length === 0 ? (
          <p className="comandas-vazio">Nenhuma mesa nesta área.</p>
        ) : (
          <ul className="mesa-list" aria-label={titulo}>
            {lista.map((mesa) => (
              <li key={mesa.id}>{linha(mesa)}</li>
            ))}
          </ul>
        )}
      </section>
    );
  }

  return (
    <section className="mesas" aria-labelledby="mesas-titulo">
      <h2 id="mesas-titulo" className="app-empty-title">
        Mesas
      </h2>
      {!online ? <Notice tone="info">Sem conexão. O mapa pode estar desatualizado.</Notice> : null}
      {aviso ? <Notice tone="info">{aviso}</Notice> : null}
      {erro ? <Notice tone="error">{erro}</Notice> : null}

      {mapa.isPending ? <Skeleton rows={5} label="Carregando mesas" /> : null}

      {mapa.isError && mapa.data === undefined ? (
        <Notice
          tone="error"
          action={
            <Button variant="secondary" onClick={() => void mapa.refetch()}>
              Tentar novamente
            </Button>
          }
        >
          Não foi possível carregar as mesas.
        </Notice>
      ) : null}

      {mapa.data && semMesas(mapa.data) ? (
        <p className="comandas-vazio">
          Nenhuma mesa para mostrar. Esta unidade não usa mesas ou ainda não cadastrou nenhuma.
        </p>
      ) : null}

      {mapa.data && !semMesas(mapa.data) ? (
        <>
          {mapa.data.areas.map((area) => grupo(area.id, area.nome, area.mesas))}
          {mapa.data.semArea.length > 0
            ? grupo("sem-area", mapa.data.areas.length > 0 ? "Outras mesas" : "Todas as mesas", mapa.data.semArea)
            : null}
        </>
      ) : null}

      <FolhaDaMesa
        mesa={mesaDaFolha}
        comandas={comandas.data ?? []}
        podeAbrir={podeAbrir}
        podeTrocar={podeTrocar}
        onClose={() => setFolha(null)}
        onAbrirOutra={(mesa) => void abrirNaMesa(mesa)}
        onTrocar={(comanda) => {
          setFolha(null);
          setTrocando(comanda.id);
        }}
      />
      <TrocarMesaSheet
        comanda={comandaParaTrocar}
        mesas={mesas}
        onClose={() => setTrocando(null)}
        onMovida={(comanda, mesa) => {
          setTrocando(null);
          setErro(undefined);
          setAviso(`${comanda.numeroTexto} mudou para ${nomeDaMesa(mesa)}.`);
        }}
      />
    </section>
  );
}
