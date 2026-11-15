import {
  Button,
  Chip,
  formatBRL,
  Icon,
  Notice,
  SearchField,
  Skeleton,
  StatusBadge,
  useFaixa,
} from "@flow4tech/design-system";
import { Link, useMatch, useNavigate } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";
import { useOnline } from "../fila/conexao";
import { usePermissoes } from "../sessao/permissoes";
import { contaDaSessao } from "../sessao/sessao";
import { useAtalhosDaTela } from "./atalhos";
import { useComandas, useMesas } from "./consultas";
import {
  contar,
  estadoDaComanda,
  type Filtro,
  filtrar,
  filtroInicial,
  filtrosOferecidos,
  minutosAberta,
  quemAtende,
  tempoAberta,
} from "./lista";
import { NovaComandaSheet } from "./NovaComandaSheet";
import { nomeExibido } from "./tipos";

const ROTULO: Record<Filtro, string> = {
  minhas: "Minhas",
  todas: "Todas",
  abertas: "Abertas",
  aguardando: "Aguardando",
};

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

/** The landing of /salao: the open comandas of the waiter (all of the unit for who may see others'), with search, filter chips with their tally, a card per comanda (value, time, waiter, state in words) and one accent action: Nova comanda. */
export function ComandasPage() {
  const permissoes = usePermissoes();
  const pode = permissoes.carregado && permissoes.pode("comanda:ver");
  const comandas = useComandas(pode);
  const mesas = useMesas(pode && permissoes.pode("comanda:abrir"));
  const agora = useAgora();
  useAquecerComanda();
  const online = useOnline();
  const faixa = useFaixa();
  const [busca, setBusca] = useState("");
  const [escolhido, setEscolhido] = useState<Filtro | null>(null);
  const [nova, setNova] = useState(false);
  const campoDeBusca = useRef<HTMLInputElement>(null);
  const navegar = useNavigate();
  const comandaAberta = useMatch({ from: "/app/lista/salao/comanda/$comandaId", shouldThrow: false }) !== undefined;
  const podeAbrir = permissoes.pode("comanda:abrir");
  // Computer shortcuts of the comandas screen (only here: this component is the screen). The menu one (`[`) is the shell's, handed by context.
  useAtalhosDaTela(faixa, comandaAberta, {
    nova: () => podeAbrir && setNova(true),
    buscar: () => campoDeBusca.current?.focus(),
    fechar: () => void navegar({ to: "/salao" }),
  });

  if (permissoes.carregado && !permissoes.pode("comanda:ver")) {
    return <Notice tone="info">Você não tem permissão para ver as comandas.</Notice>;
  }

  const verTodas = permissoes.pode("comanda:ver-comandas-de-outros");
  const oferecidos = filtrosOferecidos(verTodas);
  const filtro = escolhido !== null && oferecidos.includes(escolhido) ? escolhido : filtroInicial(verTodas);
  const conta = contaDaSessao();
  const quem = { conta, verTodas };
  const lista = filtrar(comandas.data ?? [], filtro, busca, quem, agora);
  const contagem = contar(comandas.data ?? [], quem);

  return (
    <section className="comandas" aria-labelledby="comandas-titulo">
      <h2 id="comandas-titulo" className="f4-visually-hidden">
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
          <SearchField
            ref={campoDeBusca}
            className="comandas-busca"
            label="Buscar comanda"
            placeholder={faixa === "celular" ? "Buscar mesa, balcão ou nome" : "Buscar comanda"}
            value={busca}
            onChange={(e) => setBusca(e.target.value)}
          />
          <div className="comandas-filtros" role="group" aria-label="Filtrar comandas">
            {oferecidos.map((f) => (
              <Chip key={f} selected={f === filtro} count={contagem[f]} onClick={() => setEscolhido(f)}>
                {ROTULO[f]}
              </Chip>
            ))}
          </div>
          {lista.length === 0 ? (
            <div className="comandas-nada">
              <p className="comandas-vazio">
                {busca.trim() === ""
                  ? "Nenhuma comanda neste filtro."
                  : `Nada com “${busca}”. Tente o número da comanda.`}
              </p>
              <Button
                variant="secondary"
                onClick={() => {
                  setBusca("");
                  setEscolhido(filtroInicial(verTodas));
                }}
              >
                Ver todas
              </Button>
            </div>
          ) : (
            <ul className="comanda-list" aria-label="Comandas abertas">
              {lista.map((comanda) => {
                const estado = estadoDaComanda(comanda, agora);
                const quem = quemAtende(comanda, conta);
                return (
                  <li key={comanda.id}>
                    <Link
                      to="/salao/comanda/$comandaId"
                      params={{ comandaId: comanda.id }}
                      className="comanda-card"
                      activeProps={{ "aria-current": "page" }}
                    >
                      <span className="comanda-card-nome">
                        {nomeExibido(comanda)}
                        <span className="f4-visually-hidden"> {comanda.numeroTexto}</span>
                      </span>
                      {comanda.consumoCentavos !== null ? (
                        <span className="comanda-card-valor f4-num">{formatBRL(comanda.consumoCentavos)}</span>
                      ) : (
                        <span />
                      )}
                      <span className="comanda-card-meta f4-num">
                        {tempoAberta(minutosAberta(comanda, agora))}
                        {quem ? ` · ${quem}` : ""}
                      </span>
                      <StatusBadge status={estado.status} detail={estado.detalhe} icon />
                    </Link>
                  </li>
                );
              })}
            </ul>
          )}
        </>
      ) : null}

      {podeAbrir ? (
        <div className="comandas-acao">
          <Button className="entry-submit" onClick={() => setNova(true)}>
            <Icon name="plus" /> Nova comanda
          </Button>
        </div>
      ) : null}

      <NovaComandaSheet
        janela={faixa === "computador"}
        open={nova}
        onOpenChange={setNova}
        mesas={mesas.data ?? []}
        comandas={comandas.data ?? []}
      />
    </section>
  );
}
