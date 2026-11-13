import {
  Button,
  formatBRL,
  ItemRow,
  Notice,
  RoundBar,
  Skeleton,
  StatusBadge,
  Stepper,
  UndoToast,
} from "@flow4tech/design-system";
import { useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate, useParams } from "@tanstack/react-router";
import { useEffect, useMemo, useRef, useState } from "react";
import {
  type EdicaoDaFila,
  enfileirarCancelamento,
  enfileirarEdicao,
  enfileirarRepeticao,
  enfileirarRodada,
  temAcaoViva,
  useFila,
} from "../fila/fila";
import { usePermissoes } from "../sessao/permissoes";
import { api } from "../sessao/sessao";
import {
  buscarGruposDoProduto,
  chaveDosGrupos,
  GRUPOS_STALE_MS,
  type GrupoDeOpcao,
  type Produto,
  useCategorias,
  useFavoritos,
  useFrequentes,
  useProdutos,
} from "./cardapio";
import { useComandas, useUltimaVez } from "./consultas";
import { useConta, useItens } from "./dadosDaComanda";
import {
  type AcaoDoMenu,
  AlterarItemSheet,
  CancelarComandaSheet,
  DadosDaComandaSheet,
  HistoricoSheet,
  MenuDaComandaSheet,
  RenomearSheet,
  TrocarGarcomSheet,
} from "./EdicaoSheets";
import { FecharContaSheet } from "./FecharConta";
import { CancelarItemSheet, EnviarRodadaSheet, RepetirRodadaSheet } from "./Folhas";
import { useGarcons, useHistorico } from "./historico";
import { type EscolhaDoItem, ItemSheet } from "./ItemSheet";
import { exigeEscolha, resumoDasOpcoes } from "./opcoes";
import { Picker } from "./Picker";
import { lerIgnorados, previaDaRepeticao, textoDosIgnorados } from "./repetir";
import {
  adicionar,
  adicionarConfigurado,
  type CodigoDeMotivo,
  DURACAO_DO_DESFAZER_MS,
  definirQuantidade,
  type ItemDaConta,
  type ItemEmPreparo,
  paraItensDaRodada,
  precoUnitario,
  substituirItem,
  totalEmPreparo,
  ultimaRodada,
  unidades,
} from "./rodada";
import { nomeExibido, tempoDecorrido } from "./tipos";
import { UltimaVezCard } from "./UltimaVezCard";
import { uuidv7 } from "./uuid";

/** The item sheet in use: a product being added, or a staged line being adjusted. */
interface FolhaDoItem {
  produto: Produto;
  grupos: GrupoDeOpcao[];
  /** Present when adjusting a staged line. */
  edicao?: { chave: string; escolha: EscolhaDoItem };
  /** Changes per opening, so the sheet starts clean. */
  numero: number;
}

interface Desfazer {
  rodadaId: string;
  /** How long is left of the window, counted from the moment the request left (the server counts from a later instant, so the screen never offers more than the server allows). */
  duracaoMs: number;
}

type ErroDeApi =
  | {
      codigo?: string;
      versaoAtual?: number | null;
      ignorados?: { produtoNome: string; quantidade: number | string; motivo: string }[];
    }
  | undefined;

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
  const favoritos = useFavoritos(pode && lancar);
  const navegar = useNavigate();

  const [emPreparo, setEmPreparo] = useState<ItemEmPreparo[]>([]);
  const [folhaDoItem, setFolhaDoItem] = useState<FolhaDoItem | null>(null);
  const [folhaEnviar, setFolhaEnviar] = useState(false);
  const [enviando, setEnviando] = useState(false);
  const [erroEnviar, setErroEnviar] = useState<string | undefined>(undefined);
  const [aviso, setAviso] = useState<string | null>(null);
  const [desfazer, setDesfazer] = useState<Desfazer | null>(null);
  const [desfazendo, setDesfazendo] = useState(false);
  const [folhaFechar, setFolhaFechar] = useState(false);
  const [folhaRepetir, setFolhaRepetir] = useState(false);
  const [repetindo, setRepetindo] = useState(false);
  const [erroRepetir, setErroRepetir] = useState<string | undefined>(undefined);
  const [ultimaVezUsada, setUltimaVezUsada] = useState(false);
  const [folhaMenu, setFolhaMenu] = useState(false);
  const [itemParaAlterar, setItemParaAlterar] = useState<ItemDaConta | null>(null);
  const [folhaNome, setFolhaNome] = useState(false);
  const [folhaDados, setFolhaDados] = useState(false);
  const [folhaGarcom, setFolhaGarcom] = useState(false);
  const [folhaHistorico, setFolhaHistorico] = useState(false);
  const [folhaCancelarComanda, setFolhaCancelarComanda] = useState(false);
  const [cancelandoComanda, setCancelandoComanda] = useState(false);
  const [erroCancelarComanda, setErroCancelarComanda] = useState<string | undefined>(undefined);
  const historico = useHistorico(comandaId, pode && folhaHistorico && permissoes.pode("comanda:ver-historico"));
  const garcons = useGarcons(pode && folhaGarcom && permissoes.pode("comanda:trocar-garcom"));
  const { acoes: filaAgora } = useFila();
  const [paraCancelar, setParaCancelar] = useState<ItemDaConta | null>(null);
  const [cancelando, setCancelando] = useState(false);
  const [erroCancelar, setErroCancelar] = useState<string | undefined>(undefined);
  const rodadaEmEnvio = useRef<string | null>(null);
  const rodadaRepetida = useRef<string | null>(null);
  const versao = useRef(0);

  // The version the next write carries: the highest the screen knows (what the server says, or what the last write answered).
  useEffect(() => {
    if (conta.data) {
      versao.current = Math.max(versao.current, conta.data.versao);
    }
  }, [conta.data]);

  const quantidades = useMemo(() => {
    const mapa = new Map<string, number>();
    for (const i of emPreparo) {
      mapa.set(i.produto.id, (mapa.get(i.produto.id) ?? 0) + i.quantidade);
    }

    return mapa;
  }, [emPreparo]);
  const aberturas = useRef(0);

  // The add-on groups of every product are read in the background as soon as the menu is here, a few at a time, so a tap (even offline later) finds them in the cache and a product
  // without options still goes in with one tap. A failed read is retried by the tap itself.
  const idsDoCardapio = useMemo(
    () => (produtos.data ?? []).filter((p) => !p.indisponivel).map((p) => p.id),
    [produtos.data],
  );
  useEffect(() => {
    if (!lancar) {
      return;
    }

    let cancelado = false;
    void (async () => {
      for (let i = 0; i < idsDoCardapio.length && !cancelado; i += 4) {
        await Promise.all(
          idsDoCardapio.slice(i, i + 4).map((id) =>
            queryClient.prefetchQuery({
              queryKey: chaveDosGrupos(id),
              queryFn: () => buscarGruposDoProduto(id),
              staleTime: GRUPOS_STALE_MS,
            }),
          ),
        );
      }
    })();
    return () => {
      cancelado = true;
    };
  }, [idsDoCardapio, lancar, queryClient]);
  const comanda = comandas.data?.find((c) => c.id === comandaId);
  const ultimaVez = useUltimaVez(comanda?.mesaId ?? null, pode && lancar && permissoes.pode("mesa:ver"));

  async function atualizarDados() {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ["comanda", comandaId] }),
      queryClient.invalidateQueries({ queryKey: ["comandas"] }),
    ]);
  }

  /** The round cannot go now (no signal, a server hiccup): it waits in the visible queue under the same id and goes out in order when it can. */
  function guardarNaFila(rodadaId: string, atrasoDaFila = false) {
    const total = totalEmPreparo(emPreparo);
    enfileirarRodada({
      rodadaId,
      comandaId,
      itens: paraItensDaRodada(emPreparo),
      titulo: `Rodada · ${comanda?.numeroTexto ?? ""}`.trim(),
      detalhe: `${unidades(emPreparo)} ${unidades(emPreparo) === 1 ? "item" : "itens"} · ${formatBRL(total)}`,
    });
    rodadaEmEnvio.current = null;
    setEmPreparo([]);
    setFolhaEnviar(false);
    setAviso(
      atrasoDaFila
        ? "Há ações desta comanda esperando na fila. A rodada entrou atrás delas e será enviada em ordem. Nada se perde."
        : "Sem conexão com o servidor. A rodada ficou na fila e será enviada quando der. Nada se perde.",
    );
  }

  async function enviar() {
    setErroEnviar(undefined);
    setAviso(null);
    setEnviando(true);
    // The id of the round is made once and kept until the server accepts it: a retry after a lost answer is a repeat, never a second round.
    rodadaEmEnvio.current ??= uuidv7();
    const rodadaId = rodadaEmEnvio.current;
    const inicio = Date.now();
    // With an earlier action of this comanda still waiting in the queue, a new round goes behind it even with signal: it must not overtake it.
    if (temAcaoViva(comandaId)) {
      guardarNaFila(rodadaId, navigator.onLine);
      setEnviando(false);
      return;
    }

    if (!navigator.onLine) {
      guardarNaFila(rodadaId);
      setEnviando(false);
      return;
    }

    try {
      const { data, response, error } = await api.POST("/comandas/{comandaId}/rodadas", {
        params: { path: { comandaId } },
        body: {
          rodadaId,
          versao: versao.current,
          itens: paraItensDaRodada(emPreparo),
        },
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
        setAviso(
          "Esta comanda foi alterada em outro aparelho. Atualizamos a conta; confira e toque em Enviar de novo.",
        );
      } else if (response.status === 409 && codigo === "produto-inativo") {
        setErroEnviar("Um dos itens não está mais disponível. Volte e retire-o da rodada.");
      } else if (response.status === 409 && codigo === "comanda-nao-aberta") {
        setErroEnviar("Esta comanda não está mais aberta. A rodada não foi enviada.");
      } else if (codigo === "opcao-invalida" || codigo === "opcao-inativa") {
        // The add-ons changed on the server (or a required group was left empty): the cached groups are dropped so the next tap reads them fresh.
        void queryClient.invalidateQueries({ queryKey: ["cardapio", "produto"] });
        setErroEnviar("Os adicionais de um item mudaram. Volte, ajuste o item e envie de novo.");
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
      setAviso(
        codigo === "janela-expirada" ? "Passou o prazo para desfazer a rodada." : "Não foi possível desfazer a rodada.",
      );
    } catch {
      setAviso("Sem conexão. Não foi possível desfazer a rodada.");
    } finally {
      setDesfazendo(false);
    }
  }

  async function cancelarItem(item: ItemDaConta, motivo: CodigoDeMotivo, texto: string) {
    setErroCancelar(undefined);
    // With an earlier action of this comanda still waiting in the queue, the cancel goes behind it (it could otherwise reach the server first).
    if (temAcaoViva(comandaId)) {
      enfileirarCancelamento({
        acaoId: uuidv7(),
        comandaId,
        itemId: item.id,
        motivo,
        texto: texto === "" ? null : texto,
        titulo: `Cancelar item · ${comanda?.numeroTexto ?? ""}`.trim(),
        detalhe: `${item.produtoNome} · ${item.quantidade}`,
      });
      setParaCancelar(null);
      setAviso("Há ações desta comanda esperando na fila. O cancelamento entrou atrás delas e será enviado em ordem.");
      return;
    }

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

  /** An edit of the comanda always goes through the visible queue: it is sent at once with signal, waits in order behind earlier actions and without signal, and never overtakes them. */
  function editar(edicao: EdicaoDaFila, titulo: string, detalhe: string) {
    enfileirarEdicao({
      acaoId: uuidv7(),
      comandaId,
      edicao,
      titulo: `${titulo} · ${comanda?.numeroTexto ?? ""}`.trim(),
      detalhe,
    });
  }

  function aoEscolherNoMenu(acao: AcaoDoMenu) {
    setFolhaMenu(false);
    setAviso(null);
    if (acao === "renomear") {
      setFolhaNome(true);
    } else if (acao === "dados") {
      setFolhaDados(true);
    } else if (acao === "garcom") {
      setFolhaGarcom(true);
    } else if (acao === "historico") {
      setFolhaHistorico(true);
    } else {
      setErroCancelarComanda(undefined);
      setFolhaCancelarComanda(true);
    }
  }

  /** Cancelling the whole comanda needs the server now (it is never queued): the answer decides what the screen does next. */
  async function cancelarComanda(motivo: CodigoDeMotivo, texto: string) {
    setErroCancelarComanda(undefined);
    setCancelandoComanda(true);
    try {
      const { data, response, error } = await api.POST("/comandas/{comandaId}/cancelar", {
        params: { path: { comandaId } },
        body: { versao: versao.current, motivo, texto: texto === "" ? null : texto },
      });
      if (data) {
        setFolhaCancelarComanda(false);
        await queryClient.invalidateQueries({ queryKey: ["comandas"] });
        void queryClient.invalidateQueries({ queryKey: ["mesas"] });
        await navegar({ to: "/salao" });
        return;
      }

      const codigo = (error as ErroDeApi)?.codigo;
      if (response.status === 409 && codigo === "versao-desatualizada") {
        if (typeof (error as ErroDeApi)?.versaoAtual === "number") {
          versao.current = Math.max(versao.current, Number((error as ErroDeApi)?.versaoAtual));
        }

        await atualizarDados();
        setErroCancelarComanda("Esta comanda foi alterada em outro aparelho. Atualizamos; confira e tente de novo.");
      } else if (response.status === 409 && codigo === "com-pagamentos") {
        setErroCancelarComanda("Esta comanda já tem pagamento e não pode ser cancelada.");
      } else if (response.status === 409 && codigo === "comanda-nao-aberta") {
        setFolhaCancelarComanda(false);
        await atualizarDados();
      } else if (response.status === 403) {
        setErroCancelarComanda("Você não tem permissão para cancelar comandas.");
      } else {
        setErroCancelarComanda("Não foi possível cancelar a comanda. Tente de novo.");
      }
    } catch {
      setErroCancelarComanda("Sem conexão. Cancelar a comanda precisa do servidor.");
    } finally {
      setCancelandoComanda(false);
    }
  }

  /** One tap on + : a product with nothing to choose goes straight into the round; a required group opens the item sheet. Groups come from the cache, so the common case never waits. */
  async function aoAdicionar(produto: Produto) {
    setAviso(null);
    let grupos = queryClient.getQueryData<GrupoDeOpcao[]>(chaveDosGrupos(produto.id));
    if (grupos === undefined) {
      try {
        grupos = await buscarGruposDoProduto(produto.id);
        queryClient.setQueryData(chaveDosGrupos(produto.id), grupos, { updatedAt: Date.now() - GRUPOS_STALE_MS / 2 });
      } catch {
        // No signal and the groups never came: working offline must not stop, so the product goes in plain and the waiter is told. If a required group was missed the server refuses the round
        // and the queue shows it as not sent (the waiter then adjusts the item), never as a silent loss.
        setAviso(
          `Sem sinal para ver as opções de ${produto.nome}. Entrou sem adicionais: confira se ele tem opção obrigatória. Se tiver, o envio será recusado e a rodada continua aqui para você corrigir.`,
        );
        setEmPreparo((atual) => adicionar(atual, produto));
        return;
      }
    }

    if (exigeEscolha(grupos)) {
      abrirFolha(produto, grupos);
      return;
    }

    setEmPreparo((atual) => adicionar(atual, produto));
  }

  function abrirFolha(produto: Produto, grupos: GrupoDeOpcao[], edicao?: FolhaDoItem["edicao"]) {
    aberturas.current += 1;
    setFolhaDoItem({ produto, grupos, numero: aberturas.current, ...(edicao ? { edicao } : {}) });
  }

  function aoAbrirOpcoes(produto: Produto) {
    const grupos = queryClient.getQueryData<GrupoDeOpcao[]>(chaveDosGrupos(produto.id)) ?? [];
    abrirFolha(produto, grupos);
  }

  function ajustar(item: ItemEmPreparo) {
    const grupos = queryClient.getQueryData<GrupoDeOpcao[]>(chaveDosGrupos(item.produto.id)) ?? [];
    abrirFolha(item.produto, grupos, {
      chave: item.chave,
      escolha: { opcoes: item.opcoes, observacao: item.observacao, quantidade: item.quantidade },
    });
  }

  function confirmarItem(escolha: EscolhaDoItem) {
    if (!folhaDoItem) {
      return;
    }

    const { produto, edicao } = folhaDoItem;
    setEmPreparo((atual) =>
      edicao
        ? substituirItem(atual, edicao.chave, { produto, ...escolha })
        : adicionarConfigurado(atual, { produto, ...escolha }),
    );
    setFolhaDoItem(null);
  }

  function abrirRepetir() {
    setErroRepetir(undefined);
    setAviso(null);
    setFolhaRepetir(true);
  }

  /** The repeat could not go now: it waits in the visible queue under the same round id and goes out in order. */
  function guardarRepeticaoNaFila(rodadaId: string, quantos: number) {
    enfileirarRepeticao({
      rodadaId,
      comandaId,
      titulo: `Repetir rodada · ${comanda?.numeroTexto ?? ""}`.trim(),
      detalhe: `${quantos} ${quantos === 1 ? "item" : "itens"} da última rodada`,
    });
    rodadaRepetida.current = null;
    setFolhaRepetir(false);
    setAviso("Sem conexão com o servidor. A repetição ficou na fila e será enviada quando der. Nada se perde.");
  }

  /** "Repetir agora": the server builds the new round from the last one (prices of now) and tells what it left out. */
  async function repetirRodada() {
    setErroRepetir(undefined);
    setAviso(null);
    rodadaRepetida.current ??= uuidv7();
    const rodadaId = rodadaRepetida.current;
    const quantos = previa.entram.length;
    if (!navigator.onLine) {
      guardarRepeticaoNaFila(rodadaId, quantos);
      return;
    }

    setRepetindo(true);
    const inicio = Date.now();
    try {
      const { data, response, error } = await api.POST("/comandas/{comandaId}/rodadas/repetir", {
        params: { path: { comandaId } },
        body: { rodadaId, versao: versao.current },
      });
      if (data) {
        versao.current = Number(data.rodada.comandaVersao);
        rodadaRepetida.current = null;
        setFolhaRepetir(false);
        setDesfazer({
          rodadaId: data.rodada.id,
          duracaoMs: Math.max(0, DURACAO_DO_DESFAZER_MS - (Date.now() - inicio)),
        });
        const fora = textoDosIgnorados(lerIgnorados(data.ignorados));
        if (fora !== "") {
          setAviso(fora);
        }

        await atualizarDados();
        void queryClient.invalidateQueries({ queryKey: ["cardapio", "frequentes"] });
        void queryClient.invalidateQueries({ queryKey: ["cardapio", "favoritos"] });
        return;
      }

      const falha = error as ErroDeApi;
      const codigo = falha?.codigo;
      const fora = textoDosIgnorados(lerIgnorados(falha?.ignorados));
      rodadaRepetida.current = null;
      if (response.status === 409 && codigo === "versao-desatualizada") {
        if (typeof falha?.versaoAtual === "number") {
          versao.current = Math.max(versao.current, falha.versaoAtual);
        }

        await atualizarDados();
        setErroRepetir(
          "Esta comanda foi alterada em outro aparelho. Atualizamos a conta; confira e toque em Repetir agora de novo.",
        );
      } else if (response.status === 409 && codigo === "nada-para-repetir") {
        setErroRepetir(`Nenhum item da última rodada está disponível agora.${fora === "" ? "" : ` ${fora}`}`);
      } else if (response.status === 409 && codigo === "sem-rodada-anterior") {
        setErroRepetir("Esta comanda ainda não tem rodada para repetir.");
      } else if (response.status === 409 && codigo === "comanda-nao-aberta") {
        setErroRepetir("Esta comanda não está mais aberta. A rodada não foi enviada.");
      } else if (response.status === 403) {
        setErroRepetir("Você não tem permissão para lançar itens.");
      } else if (response.status >= 500 || response.status === 408 || response.status === 429) {
        guardarRepeticaoNaFila(rodadaId, quantos);
      } else {
        setErroRepetir("Não foi possível repetir a rodada. Tente de novo.");
      }
    } catch {
      guardarRepeticaoNaFila(rodadaId, quantos);
    } finally {
      setRepetindo(false);
    }
  }

  /** "Adicionar tudo" of the last-time card: the items of that bill come into the staged round (add-ons and notes included); what is "acabou" now is named, never dropped silently. */
  function adicionarUltimaVez() {
    const tinha = ultimaVez.data;
    if (!tinha || !produtos.data) {
      return;
    }

    let proximo: ItemEmPreparo[] = [...emPreparo];
    const fora: string[] = [];
    for (const item of tinha.itens) {
      const produto = produtos.data.find((p) => p.id === item.produtoId);
      if (!produto || produto.indisponivel) {
        fora.push(`${item.quantidade}× ${item.produtoNome}`);
        continue;
      }

      proximo = adicionarConfigurado(proximo, {
        produto,
        quantidade: item.quantidade,
        opcoes: item.opcoes,
        observacao: item.observacao,
      });
    }

    setEmPreparo(proximo);
    setUltimaVezUsada(true);
    setAviso(fora.length > 0 ? `Não entraram (acabou ou saiu do cardápio): ${fora.join("; ")}.` : null);
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
  if (
    (comandas.isError && comandas.data === undefined) ||
    (conta.isError && conta.data === undefined) ||
    (itens.isError && itens.data === undefined)
  ) {
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
  const podeAlterarItem = permissoes.pode("comanda:alterar-item");
  const acoesDoMenu: AcaoDoMenu[] = (
    [
      ["renomear", "comanda:renomear"],
      ["dados", "comanda:alterar-dados"],
      ["garcom", "comanda:trocar-garcom"],
      ["historico", "comanda:ver-historico"],
      ["cancelar", "comanda:cancelar"],
    ] as const
  )
    .filter(([, permissao]) => permissoes.pode(permissao))
    .map(([acao]) => acao);
  const podeDesfazer = permissoes.pode("comanda:desfazer-rodada");
  const podeFechar = permissoes.pode("comanda:fechar") && permissoes.pode("comanda:receber-pagamento");
  const ultima = ultimaRodada(itens.data);
  const temUltima = ultima !== null;
  const previa = previaDaRepeticao(ultima ?? [], produtos.data ?? []);
  const filaDaComanda = filaAgora.some((a) => a.comandaId === comandaId && a.estado !== "falhou");
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
        <div className="comanda-status-linha">
          <StatusBadge status="aberta" detail={tempoDecorrido(comanda.abertaEm, Date.now())} />
          {acoesDoMenu.length > 0 ? (
            <Button variant="secondary" onClick={() => setFolhaMenu(true)}>
              Mais
            </Button>
          ) : null}
        </div>
      </header>

      {aviso ? <Notice tone="info">{aviso}</Notice> : null}

      <div className="comanda-conta">
        {lancados.length === 0 && emPreparo.length === 0 ? (
          <p className="comandas-vazio">Nenhum item ainda. Escolha abaixo.</p>
        ) : null}
        <ul className="conta-itens" aria-label="Itens da conta">
          {lancados.map((i) => (
            <ItemRow
              key={i.id}
              quantity={i.quantidade}
              name={i.produtoNome}
              modifier={[resumoDasOpcoes(i.opcoes), i.observacao].filter(Boolean).join(" · ") || undefined}
              priceCents={i.unitarioCentavos}
              action={
                podeAlterarItem || podeCancelar ? (
                  <>
                    {podeAlterarItem ? (
                      <button
                        type="button"
                        className="f4-row-action"
                        aria-label={`Alterar ${i.produtoNome}`}
                        onClick={() => setItemParaAlterar(i)}
                      >
                        Alterar
                      </button>
                    ) : null}
                    {podeCancelar ? (
                      <button
                        type="button"
                        className="f4-row-action"
                        aria-label={`Cancelar ${i.produtoNome}`}
                        onClick={() => setParaCancelar(i)}
                      >
                        Cancelar
                      </button>
                    ) : null}
                  </>
                ) : undefined
              }
            />
          ))}
          {emPreparo.map((i) => (
            <ItemRow
              key={`preparo-${i.chave}`}
              quantity={i.quantidade}
              name={i.produto.nome}
              modifier={["Não enviado", resumoDasOpcoes(i.opcoes), i.observacao].filter(Boolean).join(" · ")}
              priceCents={precoUnitario(i)}
              action={
                <>
                  <Stepper
                    label={i.produto.nome}
                    value={i.quantidade}
                    min={0}
                    onChange={(q) => setEmPreparo((atual) => definirQuantidade(atual, i.chave, q))}
                  />
                  <button
                    type="button"
                    className="f4-row-action"
                    aria-label={`Ajustar ${i.produto.nome}`}
                    onClick={() => ajustar(i)}
                  >
                    Ajustar
                  </button>
                </>
              }
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
          favoritos={favoritos.data ?? []}
          frequentes={frequentes.data ?? []}
          topo={
            ultimaVez.data && !ultimaVezUsada ? (
              <UltimaVezCard ultimaVez={ultimaVez.data} agora={Date.now()} onAdicionarTudo={adicionarUltimaVez} />
            ) : undefined
          }
          categorias={categorias.data ?? []}
          quantidades={quantidades}
          onAdicionar={(produto: Produto) => void aoAdicionar(produto)}
          onOpcoes={aoAbrirOpcoes}
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
            <RoundBar
              itens={faltaUnidades}
              totalCentavos={totalEmPreparo(emPreparo)}
              action={<Button onClick={() => setFolhaEnviar(true)}>Enviar</Button>}
            />
          ) : temUltima ? (
            <>
              {filaDaComanda ? (
                <p className="comanda-barra-aviso">Há ações desta comanda na fila. Repita depois que forem enviadas.</p>
              ) : null}
              <Button className="entry-submit" onClick={abrirRepetir} disabled={!produtos.data || filaDaComanda}>
                Repetir última rodada
              </Button>
            </>
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
        <UndoToast
          key={desfazer.rodadaId}
          message="Rodada enviada"
          durationMs={desfazer.duracaoMs}
          loading={desfazendo}
          onUndo={() => void desfazerRodada()}
          onExpire={() => setDesfazer(null)}
        />
      ) : null}

      {podeFechar ? (
        <FecharContaSheet
          open={folhaFechar}
          onOpenChange={setFolhaFechar}
          comandaId={comandaId}
          nome={nomeExibido(comanda)}
          conta={conta.data}
          versao={versao}
          onAtualizar={atualizarDados}
        />
      ) : null}
      {folhaDoItem ? (
        <ItemSheet
          key={folhaDoItem.numero}
          open
          onOpenChange={(aberto) => {
            if (!aberto) {
              setFolhaDoItem(null);
            }
          }}
          produto={folhaDoItem.produto}
          grupos={folhaDoItem.grupos}
          inicial={folhaDoItem.edicao?.escolha}
          onConfirmar={confirmarItem}
        />
      ) : null}
      <EnviarRodadaSheet
        open={folhaEnviar}
        onOpenChange={setFolhaEnviar}
        itens={emPreparo}
        enviando={enviando}
        erro={erroEnviar}
        onEnviar={() => void enviar()}
      />
      <RepetirRodadaSheet
        open={folhaRepetir}
        onOpenChange={setFolhaRepetir}
        previa={previa}
        enviando={repetindo}
        erro={erroRepetir}
        onRepetir={() => void repetirRodada()}
      />
      <MenuDaComandaSheet
        open={folhaMenu}
        onOpenChange={setFolhaMenu}
        acoes={acoesDoMenu}
        onEscolher={aoEscolherNoMenu}
      />
      <AlterarItemSheet
        item={itemParaAlterar}
        onOpenChange={(aberto) => {
          if (!aberto) {
            setItemParaAlterar(null);
          }
        }}
        onSalvar={(quantidade, observacao) => {
          if (itemParaAlterar) {
            editar(
              { tipo: "item", itemId: itemParaAlterar.id, quantidade, observacao },
              "Alterar item",
              `${itemParaAlterar.produtoNome} · ${quantidade}`,
            );
          }

          setItemParaAlterar(null);
        }}
      />
      <RenomearSheet
        open={folhaNome}
        onOpenChange={setFolhaNome}
        comanda={comanda}
        onSalvar={(identificacao) => {
          editar({ tipo: "nome", identificacao }, "Renomear", identificacao);
          setFolhaNome(false);
        }}
      />
      <DadosDaComandaSheet
        open={folhaDados}
        onOpenChange={setFolhaDados}
        comanda={comanda}
        onSalvar={(pessoas, observacao) => {
          editar(
            { tipo: "dados", pessoas, observacao },
            "Pessoas e observação",
            pessoas === null ? "Sem número de pessoas" : `${pessoas} pessoas`,
          );
          setFolhaDados(false);
        }}
      />
      <TrocarGarcomSheet
        open={folhaGarcom}
        onOpenChange={setFolhaGarcom}
        garcomAtualId={comanda.garcomId}
        garcons={{
          carregando: garcons.isPending && !garcons.isError,
          erro: garcons.isError,
          dados: garcons.data,
          recarregar: () => void garcons.refetch(),
        }}
        onTrocar={(garcomId) => {
          editar(
            { tipo: "garcom", garcomId },
            "Trocar garçom",
            garcons.data?.find((g) => g.id === garcomId)?.nome ?? "",
          );
          setFolhaGarcom(false);
        }}
      />
      <HistoricoSheet
        open={folhaHistorico}
        onOpenChange={setFolhaHistorico}
        agora={new Date()}
        historico={{
          carregando: historico.isPending && !historico.isError,
          erro: historico.isError,
          eventos: historico.data,
          recarregar: () => void historico.refetch(),
        }}
      />
      <CancelarComandaSheet
        open={folhaCancelarComanda}
        onOpenChange={setFolhaCancelarComanda}
        numeroTexto={comanda.numeroTexto}
        bloqueio={
          !navigator.onLine
            ? "Sem conexão. Cancelar a comanda precisa do servidor."
            : filaDaComanda
              ? "Há ações desta comanda na fila. Cancele depois que forem enviadas."
              : null
        }
        enviando={cancelandoComanda}
        erro={erroCancelarComanda}
        onCancelar={(motivo, texto) => void cancelarComanda(motivo, texto)}
      />
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
