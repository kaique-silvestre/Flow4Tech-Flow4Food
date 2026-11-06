import { Button, formatBRL, ItemRow, Notice, RoundBar, Skeleton, StatusBadge, Stepper, UndoToast } from "@flow4tech/design-system";
import { useQueryClient } from "@tanstack/react-query";
import { Link, useParams } from "@tanstack/react-router";
import { useEffect, useMemo, useRef, useState } from "react";
import { enfileirarRodada } from "../fila/fila";
import { usePermissoes } from "../sessao/permissoes";
import { api } from "../sessao/sessao";
import { useCategorias, useFrequentes, useProdutos, type Produto } from "./cardapio";
import { useComandas } from "./consultas";
import { useConta, useItens } from "./dadosDaComanda";
import { FecharContaSheet } from "./FecharConta";
import { CancelarItemSheet, EnviarRodadaSheet } from "./Folhas";
import { Picker } from "./Picker";
import { adicionar, definirQuantidade, DURACAO_DO_DESFAZER_MS, totalEmPreparo, ultimaRodada, unidades, type CodigoDeMotivo, type ItemDaConta, type ItemEmPreparo } from "./rodada";
import { nomeExibido, tempoDecorrido } from "./tipos";
import { uuidv7 } from "./uuid";

interface Desfazer {
  rodadaId: string;
  /** How long is left of the window, counted from the moment the request left (the server counts from a later instant, so the screen never offers more than the server allows). */
  duracaoMs: number;
}

type ErroDeApi = { codigo?: string; versaoAtual?: number | null } | undefined;

/**
 * The comanda screen (docs/design/refs/salao/02.png, 03.png, 07.png): the bill on top, the picker below, the staged round with steppers, the round bar, the send sheet, the 8-second undo
 * and the cancel of an item. Every write carries the comanda version the screen last saw; a stale one is told and refreshed without losing what is staged.
 */
export function ComandaPage() {
  const { comandaId } = useParams({ from: "/app/salao/comanda/$comandaId" });
  const queryClient = useQueryClient();
  const permissoes = usePermissoes();
  const pode = permissoes.carregado && permissoes.pode("comanda:ver");
  const lancar = permissoes.pode("comanda:lancar-item");
  const comandas = useComandas(pode);
  const conta = useConta(comandaId, pode);
  const itens = useItens(comandaId, pode);
  const produtos = useProdutos(pode && lancar);
  const categorias = useCategorias(pode && lancar);
  const frequentes = useFrequentes(pode && lancar);

  const [emPreparo, setEmPreparo] = useState<ItemEmPreparo[]>([]);
  const [folhaEnviar, setFolhaEnviar] = useState(false);
  const [enviando, setEnviando] = useState(false);
  const [erroEnviar, setErroEnviar] = useState<string | undefined>(undefined);
  const [aviso, setAviso] = useState<string | null>(null);
  const [desfazer, setDesfazer] = useState<Desfazer | null>(null);
  const [desfazendo, setDesfazendo] = useState(false);
  const [folhaFechar, setFolhaFechar] = useState(false);
  const [paraCancelar, setParaCancelar] = useState<ItemDaConta | null>(null);
  const [cancelando, setCancelando] = useState(false);
  const [erroCancelar, setErroCancelar] = useState<string | undefined>(undefined);
  const rodadaEmEnvio = useRef<string | null>(null);
  const versao = useRef(0);

  // The version the next write carries: the highest the screen knows (what the server says, or what the last write answered).
  useEffect(() => {
    if (conta.data) {
      versao.current = Math.max(versao.current, conta.data.versao);
    }
  }, [conta.data]);

  const quantidades = useMemo(() => new Map(emPreparo.map((i) => [i.produto.id, i.quantidade] as const)), [emPreparo]);
  const comanda = comandas.data?.find((c) => c.id === comandaId);

  async function atualizarDados() {
    await Promise.all([queryClient.invalidateQueries({ queryKey: ["comanda", comandaId] }), queryClient.invalidateQueries({ queryKey: ["comandas"] })]);
  }

  /** The round cannot go now (no signal, a server hiccup): it waits in the visible queue under the same id and goes out in order when it can. */
  function guardarNaFila(rodadaId: string) {
    const total = totalEmPreparo(emPreparo);
    enfileirarRodada({
      rodadaId,
      comandaId,
      itens: emPreparo.map((i) => ({ produtoId: i.produto.id, quantidade: i.quantidade, observacao: null })),
      titulo: `Rodada · ${comanda?.numeroTexto ?? ""}`.trim(),
      detalhe: `${unidades(emPreparo)} ${unidades(emPreparo) === 1 ? "item" : "itens"} · ${formatBRL(total)}`,
    });
    rodadaEmEnvio.current = null;
    setEmPreparo([]);
    setFolhaEnviar(false);
    setAviso("Sem conexão com o servidor. A rodada ficou na fila e será enviada quando der. Nada se perde.");
  }

  async function enviar() {
    setErroEnviar(undefined);
    setAviso(null);
    setEnviando(true);
    // The id of the round is made once and kept until the server accepts it: a retry after a lost answer is a repeat, never a second round.
    const rodadaId = (rodadaEmEnvio.current ??= uuidv7());
    const inicio = Date.now();
    if (!navigator.onLine) {
      guardarNaFila(rodadaId);
      setEnviando(false);
      return;
    }

    try {
      const { data, response, error } = await api.POST("/comandas/{comandaId}/rodadas", {
        params: { path: { comandaId } },
        body: { rodadaId, versao: versao.current, itens: emPreparo.map((i) => ({ produtoId: i.produto.id, quantidade: i.quantidade, observacao: null })) },
      });
      if (data) {
        versao.current = Number(data.comandaVersao);
        rodadaEmEnvio.current = null;
        setEmPreparo([]);
        setFolhaEnviar(false);
        setDesfazer({ rodadaId, duracaoMs: Math.max(0, DURACAO_DO_DESFAZER_MS - (Date.now() - inicio)) });
        await atualizarDados();
        void queryClient.invalidateQueries({ queryKey: ["cardapio", "frequentes"] });
        return;
      }

      const codigo = (error as ErroDeApi)?.codigo;
      if (response.status === 409 && codigo === "versao-desatualizada") {
        // Another device changed the comanda: the screen refreshes and the staged round stays; nothing was written.
        setFolhaEnviar(false);
        await atualizarDados();
        setAviso("Esta comanda foi alterada em outro aparelho. Atualizamos a conta; confira e toque em Enviar de novo.");
      } else if (response.status === 409 && codigo === "produto-inativo") {
        setErroEnviar("Um dos itens não está mais disponível. Volte e retire-o da rodada.");
      } else if (response.status === 409 && codigo === "comanda-nao-aberta") {
        setErroEnviar("Esta comanda não está mais aberta. A rodada não foi enviada.");
      } else if (response.status === 403) {
        setErroEnviar("Você não tem permissão para lançar itens.");
      } else if (response.status >= 500 || response.status === 408 || response.status === 429) {
        guardarNaFila(rodadaId);
      } else {
        setErroEnviar("Não foi possível enviar. Confira os itens e tente de novo.");
      }
    } catch {
      guardarNaFila(rodadaId);
    } finally {
      setEnviando(false);
    }
  }

  async function desfazerRodada(tentativa = 0) {
    if (!desfazer) {
      return;
    }

    setDesfazendo(true);
    setAviso(null);
    try {
      const { data, response, error } = await api.POST("/comandas/{comandaId}/rodadas/{rodadaId}/desfazer", {
        params: { path: { comandaId, rodadaId: desfazer.rodadaId } },
        body: { versao: versao.current },
      });
      if (data) {
        versao.current = Number(data.comandaVersao);
        setDesfazer(null);
        await atualizarDados();
        setAviso("Rodada desfeita. Os itens saíram da conta.");
        return;
      }

      const codigo = (error as ErroDeApi)?.codigo;
      if (response.status === 409 && codigo === "versao-desatualizada" && tentativa === 0) {
        const atual = (error as ErroDeApi)?.versaoAtual;
        if (typeof atual === "number") {
          versao.current = Math.max(versao.current, atual);
          await desfazerRodada(1);
          return;
        }
      }

      setDesfazer(null);
      setAviso(codigo === "janela-expirada" ? "Passou o prazo para desfazer a rodada." : "Não foi possível desfazer a rodada.");
    } catch {
      setAviso("Sem conexão. Não foi possível desfazer a rodada.");
    } finally {
      setDesfazendo(false);
    }
  }

  async function cancelarItem(item: ItemDaConta, motivo: CodigoDeMotivo, texto: string) {
    setErroCancelar(undefined);
    setCancelando(true);
    try {
      const { data, response, error } = await api.POST("/comandas/{comandaId}/itens/{itemId}/cancelar", {
        params: { path: { comandaId, itemId: item.id } },
        body: { versao: versao.current, motivo, texto: texto === "" ? null : texto },
      });
      if (data) {
        versao.current = Number(data.comandaVersao);
        setParaCancelar(null);
        await atualizarDados();
        return;
      }

      const codigo = (error as ErroDeApi)?.codigo;
      if (response.status === 409 && codigo === "versao-desatualizada") {
        await atualizarDados();
        setErroCancelar("Esta comanda foi alterada em outro aparelho. Atualizamos; tente de novo.");
      } else if (response.status === 409 && codigo === "valor-ja-recebido") {
        setErroCancelar("Este item já foi pago na conta e não pode ser cancelado.");
      } else if (response.status === 409 && codigo === "item-ja-cancelado") {
        setParaCancelar(null);
        await atualizarDados();
      } else if (response.status === 403) {
        setErroCancelar("Você não tem permissão para cancelar itens.");
      } else {
        setErroCancelar("Não foi possível cancelar o item. Tente de novo.");
      }
    } catch {
      setErroCancelar("Sem conexão. Tente de novo.");
    } finally {
      setCancelando(false);
    }
  }

  function repetirRodada() {
    const ultima = ultimaRodada(itens.data ?? []);
    if (!ultima || !produtos.data) {
      return;
    }

    let proximo: ItemEmPreparo[] = [...emPreparo];
    for (const item of ultima) {
      const produto = produtos.data.find((p) => p.id === item.produtoId);
      if (produto) {
        for (let n = 0; n < item.quantidade; n += 1) {
          proximo = adicionar(proximo, produto);
        }
      }
    }

    setEmPreparo(proximo);
  }

  if (permissoes.carregado && !permissoes.pode("comanda:ver")) {
    return <Notice tone="info">Você não tem permissão para ver as comandas.</Notice>;
  }

  if (comandas.data === undefined || conta.data === undefined || itens.data === undefined) {
    if (!(comandas.isError || conta.isError || itens.isError)) {
      return <Skeleton rows={4} label="Carregando a comanda" />;
    }
  }

  // Offline the screen keeps what it already loaded: an error is shown only when there is nothing to show.
  if ((comandas.isError && comandas.data === undefined) || (conta.isError && conta.data === undefined) || (itens.isError && itens.data === undefined)) {
    return (
      <Notice
        tone="error"
        action={
          <Button variant="secondary" onClick={() => void atualizarDados()}>
            Tentar novamente
          </Button>
        }
      >
        Não foi possível carregar a comanda.
      </Notice>
    );
  }

  if (!comanda || conta.data === undefined || itens.data === undefined) {
    return (
      <Notice tone="info" action={<Link to="/salao">Voltar às comandas</Link>}>
        Esta comanda não está mais aberta ou não é sua.
      </Notice>
    );
  }

  const lancados = itens.data.filter((i) => i.estado === "lancado");
  const podeCancelar = permissoes.pode("comanda:cancelar-item");
  const podeDesfazer = permissoes.pode("comanda:desfazer-rodada");
  const podeFechar = permissoes.pode("comanda:fechar") && permissoes.pode("comanda:receber-pagamento");
  const temUltima = ultimaRodada(itens.data) !== null;
  const faltaUnidades = unidades(emPreparo);

  return (
    <section className="comanda" aria-labelledby="comanda-titulo">
      <header className="comanda-topo">
        <p className="comanda-voltar">
          <Link to="/salao">Comandas</Link> › {comanda.numeroTexto}
        </p>
        <div className="comanda-titulo-linha">
          <h2 id="comanda-titulo" className="app-empty-title">
            {nomeExibido(comanda)}
          </h2>
          <p className="comanda-total f4-num" aria-label="Total da conta">
            {formatBRL(conta.data.totalCentavos)}
          </p>
        </div>
        <StatusBadge status="aberta" detail={tempoDecorrido(comanda.abertaEm, Date.now())} />
      </header>

      {aviso ? <Notice tone="info">{aviso}</Notice> : null}

      <div className="comanda-conta">
        {lancados.length === 0 && emPreparo.length === 0 ? <p className="comandas-vazio">Nenhum item ainda. Escolha abaixo.</p> : null}
        <ul className="conta-itens" aria-label="Itens da conta">
          {lancados.map((i) => (
            <ItemRow
              key={i.id}
              quantity={i.quantidade}
              name={i.produtoNome}
              modifier={i.observacao ?? undefined}
              priceCents={i.precoCentavos}
              action={
                podeCancelar ? (
                  <button type="button" className="f4-row-action" aria-label={`Cancelar ${i.produtoNome}`} onClick={() => setParaCancelar(i)}>
                    Cancelar
                  </button>
                ) : undefined
              }
            />
          ))}
          {emPreparo.map((i) => (
            <ItemRow
              key={`preparo-${i.produto.id}`}
              quantity={i.quantidade}
              name={i.produto.nome}
              modifier="Não enviado"
              priceCents={i.produto.precoCentavos}
              action={<Stepper label={i.produto.nome} value={i.quantidade} min={0} onChange={(q) => setEmPreparo((atual) => definirQuantidade(atual, i.produto.id, q))} />}
            />
          ))}
        </ul>
        {conta.data.taxaServicoCentavos > 0 ? (
          <p className="conta-taxa f4-num">
            Serviço <span>{formatBRL(conta.data.taxaServicoCentavos)}</span>
          </p>
        ) : null}
      </div>

      {lancar ? (
        <Picker
          produtos={produtos.data ?? []}
          frequentes={frequentes.data ?? []}
          categorias={categorias.data ?? []}
          quantidades={quantidades}
          onAdicionar={(produto: Produto) => setEmPreparo((atual) => adicionar(atual, produto))}
          carregando={produtos.isPending || categorias.isPending}
          erro={produtos.isError || categorias.isError}
          onTentarDeNovo={() => {
            void produtos.refetch();
            void categorias.refetch();
          }}
        />
      ) : null}

      {lancar ? (
        <div className="comanda-barra">
          {faltaUnidades > 0 ? (
            <RoundBar itens={faltaUnidades} totalCentavos={totalEmPreparo(emPreparo)} action={<Button onClick={() => setFolhaEnviar(true)}>Enviar</Button>} />
          ) : temUltima ? (
            <Button className="entry-submit" onClick={repetirRodada} disabled={!produtos.data}>
              Repetir rodada
            </Button>
          ) : null}
        </div>
      ) : null}

      {podeFechar && faltaUnidades === 0 ? (
        <div className="comanda-fechar">
          <Button variant="secondary" onClick={() => setFolhaFechar(true)}>
            Fechar conta
          </Button>
        </div>
      ) : null}

      {desfazer && podeDesfazer ? (
        <UndoToast key={desfazer.rodadaId} message="Rodada enviada" durationMs={desfazer.duracaoMs} loading={desfazendo} onUndo={() => void desfazerRodada()} onExpire={() => setDesfazer(null)} />
      ) : null}

      {podeFechar ? <FecharContaSheet open={folhaFechar} onOpenChange={setFolhaFechar} comandaId={comandaId} nome={nomeExibido(comanda)} conta={conta.data} versao={versao} onAtualizar={atualizarDados} /> : null}
      <EnviarRodadaSheet open={folhaEnviar} onOpenChange={setFolhaEnviar} itens={emPreparo} enviando={enviando} erro={erroEnviar} onEnviar={() => void enviar()} />
      <CancelarItemSheet
        item={paraCancelar}
        onOpenChange={(aberto) => {
          if (!aberto) {
            setParaCancelar(null);
            setErroCancelar(undefined);
          }
        }}
        enviando={cancelando}
        erro={erroCancelar}
        onCancelar={(motivo, texto) => paraCancelar && void cancelarItem(paraCancelar, motivo, texto)}
      />
    </section>
  );
}
