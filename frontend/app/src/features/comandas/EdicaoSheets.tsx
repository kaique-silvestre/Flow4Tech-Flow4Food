import { BottomSheet, Button, Chip, Notice, Skeleton, Stepper, TextField } from "@flow4tech/design-system";
import { useEffect, useState } from "react";
import "./movimento.css";
import { type EventoDoHistorico, type Garcom, horaDoEvento, linhaDoHistorico } from "./historico";
import { type CodigoDeMotivo, type ItemDaConta, MOTIVOS } from "./rodada";
import type { Comanda } from "./tipos";

export type AcaoDoMenu =
  | "desconto"
  | "transferir"
  | "separar"
  | "juntar"
  | "dividir"
  | "renomear"
  | "dados"
  | "garcom"
  | "historico"
  | "cancelar";

interface MenuProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Only the actions the caller may do (the server still decides each one). */
  acoes: readonly AcaoDoMenu[];
  onEscolher: (acao: AcaoDoMenu) => void;
}

const ROTULOS: Record<AcaoDoMenu, string> = {
  desconto: "Desconto na comanda",
  transferir: "Transferir itens",
  separar: "Separar itens",
  juntar: "Juntar comandas",
  dividir: "Dividir a conta",
  renomear: "Renomear comanda",
  dados: "Pessoas e observação",
  garcom: "Trocar de garçom",
  historico: "Histórico",
  cancelar: "Cancelar comanda",
};

const DA_CONTA: ReadonlySet<AcaoDoMenu> = new Set(["desconto", "transferir", "separar", "juntar", "dividir"]);

/** The comanda menu: one row per action the caller may do. */
export function MenuDaComandaSheet({ open, onOpenChange, acoes, onEscolher }: MenuProps) {
  return (
    <BottomSheet open={open} onOpenChange={onOpenChange} title="Mais ações">
      <ul className="menu-lista" aria-label="Ações da comanda">
        {acoes.map((acao, i) => (
          <li key={acao} data-grupo-novo={i > 0 && DA_CONTA.has(acoes[i - 1] as AcaoDoMenu) && !DA_CONTA.has(acao)}>
            <button type="button" className="menu-linha" data-acao={acao} onClick={() => onEscolher(acao)}>
              {ROTULOS[acao]}
            </button>
          </li>
        ))}
      </ul>
    </BottomSheet>
  );
}

interface AlterarItemProps {
  item: ItemDaConta | null;
  onOpenChange: (open: boolean) => void;
  onSalvar: (quantidade: number, observacao: string | null) => void;
}

/** Quantity and note of a launched item. To remove it, cancel it (that asks for a reason). The price stays the one frozen at launch. */
export function AlterarItemSheet({ item, onOpenChange, onSalvar }: AlterarItemProps) {
  const [quantidade, setQuantidade] = useState(1);
  const [observacao, setObservacao] = useState("");
  // biome-ignore lint/correctness/useExhaustiveDependencies: the form restarts when ANOTHER item is opened, so the item id is the trigger.
  useEffect(() => {
    setQuantidade(item?.quantidade ?? 1);
    setObservacao(item?.observacao ?? "");
  }, [item?.id]);

  const igual = item !== null && quantidade === item.quantidade && observacao.trim() === (item.observacao ?? "");
  return (
    <BottomSheet
      open={item !== null}
      onOpenChange={onOpenChange}
      title="Alterar item"
      description={item ? `${item.produtoNome}. O preço continua o do lançamento.` : undefined}
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            Voltar
          </Button>
          <Button
            disabled={igual}
            onClick={() => onSalvar(quantidade, observacao.trim() === "" ? null : observacao.trim())}
          >
            Salvar
          </Button>
        </>
      }
    >
      <div className="alterar-quantidade">
        <span>Quantidade</span>
        <Stepper label={item?.produtoNome ?? "Item"} value={quantidade} min={1} onChange={setQuantidade} />
      </div>
      <TextField
        label="Observação"
        value={observacao}
        maxLength={200}
        autoComplete="off"
        onChange={(e) => setObservacao(e.target.value)}
      />
    </BottomSheet>
  );
}

interface NomeProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  comanda: Pick<Comanda, "identificacao">;
  onSalvar: (identificacao: string) => void;
}

export function RenomearSheet({ open, onOpenChange, comanda, onSalvar }: NomeProps) {
  const [nome, setNome] = useState(comanda.identificacao);
  useEffect(() => {
    if (open) {
      setNome(comanda.identificacao);
    }
  }, [open, comanda.identificacao]);

  const limpo = nome.trim().replace(/\s+/g, " ");
  const valido = limpo.length >= 1 && limpo.length <= 40 && limpo !== comanda.identificacao;
  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Renomear comanda"
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            Voltar
          </Button>
          <Button disabled={!valido} onClick={() => onSalvar(limpo)}>
            Salvar
          </Button>
        </>
      }
    >
      <TextField
        label="Nome da comanda"
        value={nome}
        maxLength={40}
        autoComplete="off"
        onChange={(e) => setNome(e.target.value)}
      />
    </BottomSheet>
  );
}

interface DadosProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  comanda: Pick<Comanda, "pessoas" | "observacao">;
  onSalvar: (pessoas: number | null, observacao: string | null) => void;
}

/** Head count (1 to 999, or empty) and the general note, saved together. */
export function DadosDaComandaSheet({ open, onOpenChange, comanda, onSalvar }: DadosProps) {
  const [pessoas, setPessoas] = useState("");
  const [observacao, setObservacao] = useState("");
  useEffect(() => {
    if (open) {
      setPessoas(comanda.pessoas === null ? "" : String(comanda.pessoas));
      setObservacao(comanda.observacao ?? "");
    }
  }, [open, comanda.pessoas, comanda.observacao]);

  const numero = pessoas.trim() === "" ? null : Number(pessoas);
  const pessoasOk = numero === null || (Number.isInteger(numero) && numero >= 1 && numero <= 999);
  const nota = observacao.trim() === "" ? null : observacao.trim();
  const igual = numero === comanda.pessoas && nota === comanda.observacao;
  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Pessoas e observação"
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            Voltar
          </Button>
          <Button disabled={!pessoasOk || igual} onClick={() => onSalvar(numero, nota)}>
            Salvar
          </Button>
        </>
      }
    >
      <TextField
        label="Número de pessoas"
        inputMode="numeric"
        value={pessoas}
        maxLength={3}
        autoComplete="off"
        error={pessoasOk ? undefined : "Use um número de 1 a 999."}
        onChange={(e) => setPessoas(e.target.value.replace(/\D/g, ""))}
      />
      <TextField
        label="Observação da comanda"
        value={observacao}
        maxLength={200}
        autoComplete="off"
        onChange={(e) => setObservacao(e.target.value)}
      />
    </BottomSheet>
  );
}

interface GarcomProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  garcomAtualId: string;
  garcons: { carregando: boolean; erro: boolean; dados: readonly Garcom[] | undefined; recarregar: () => void };
  onTrocar: (garcomId: string) => void;
}

/** One tap picks the account, a second confirms; the current waiter is not offered. */
export function TrocarGarcomSheet({ open, onOpenChange, garcomAtualId, garcons, onTrocar }: GarcomProps) {
  const [escolhido, setEscolhido] = useState<string | null>(null);
  useEffect(() => {
    if (open) {
      setEscolhido(null);
    }
  }, [open]);

  const outros = (garcons.dados ?? []).filter((g) => g.id !== garcomAtualId);
  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Trocar de garçom"
      description="A comanda passa a ser de quem você escolher."
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            Voltar
          </Button>
          <Button disabled={escolhido === null} onClick={() => escolhido && onTrocar(escolhido)}>
            Trocar
          </Button>
        </>
      }
    >
      {garcons.carregando ? (
        <Skeleton rows={3} label="Carregando a equipe" />
      ) : garcons.erro ? (
        <Notice
          tone="error"
          action={
            <Button variant="secondary" onClick={garcons.recarregar}>
              Tentar novamente
            </Button>
          }
        >
          Não foi possível carregar a equipe.
        </Notice>
      ) : outros.length === 0 ? (
        <p className="comandas-vazio">Não há outra pessoa na equipe desta unidade.</p>
      ) : (
        <div className="motivos" role="group" aria-label="Garçom">
          {outros.map((g) => (
            <Chip key={g.id} selected={escolhido === g.id} onClick={() => setEscolhido(g.id)}>
              {g.nome}
            </Chip>
          ))}
        </div>
      )}
    </BottomSheet>
  );
}

interface HistoricoProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  historico: {
    carregando: boolean;
    erro: boolean;
    eventos: readonly EventoDoHistorico[] | undefined;
    recarregar: () => void;
  };
  agora: Date;
}

/** The timeline: time, who, what happened and the detail worth reading. States: loading, error with retry, empty. */
export function HistoricoSheet({ open, onOpenChange, historico, agora }: HistoricoProps) {
  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Histórico"
      footer={
        <Button variant="secondary" onClick={() => onOpenChange(false)}>
          Fechar
        </Button>
      }
    >
      {historico.carregando ? (
        <Skeleton rows={4} label="Carregando o histórico" />
      ) : historico.erro && historico.eventos === undefined ? (
        <Notice
          tone="error"
          action={
            <Button variant="secondary" onClick={historico.recarregar}>
              Tentar novamente
            </Button>
          }
        >
          Não foi possível carregar o histórico.
        </Notice>
      ) : (historico.eventos ?? []).length === 0 ? (
        <p className="comandas-vazio">Nada registrado ainda.</p>
      ) : (
        <ol className="historico" aria-label="Linha do tempo da comanda">
          {(historico.eventos ?? []).map((e) => {
            const linha = linhaDoHistorico(e);
            return (
              <li key={e.id} className="historico-linha">
                <span className="historico-hora f4-num">{horaDoEvento(e.quando, agora)}</span>
                <span>
                  <span className="historico-titulo">{linha.titulo}</span>
                  {linha.detalhe ? <span className="enviar-detalhe">{linha.detalhe}</span> : null}
                  {e.usuarioNome ? <span className="enviar-detalhe">{e.usuarioNome}</span> : null}
                </span>
              </li>
            );
          })}
        </ol>
      )}
    </BottomSheet>
  );
}

interface CancelarComandaProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  numeroTexto: string;
  /** What blocks it now (no signal, actions of the comanda waiting in the queue); null when it can go. */
  bloqueio: string | null;
  enviando: boolean;
  erro: string | undefined;
  onCancelar: (motivo: CodigoDeMotivo, texto: string) => void;
}

/** Cancelling a whole comanda: reason from the standard list, then an explicit confirmation. It needs a connection (it never waits in the queue). */
export function CancelarComandaSheet({
  open,
  onOpenChange,
  numeroTexto,
  bloqueio,
  enviando,
  erro,
  onCancelar,
}: CancelarComandaProps) {
  const [motivo, setMotivo] = useState<CodigoDeMotivo | null>(null);
  const [texto, setTexto] = useState("");
  const [confirmando, setConfirmando] = useState(false);
  useEffect(() => {
    if (open) {
      setMotivo(null);
      setTexto("");
      setConfirmando(false);
    }
  }, [open]);

  const valido = motivo !== null && (motivo !== "outro" || texto.trim() !== "");
  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title={confirmando ? `Cancelar a comanda ${numeroTexto}?` : "Cancelar comanda"}
      description={
        confirmando
          ? "Todos os itens saem da conta. Isso não pode ser desfeito."
          : "Só vale sem pagamentos e precisa de conexão. Escolha o motivo."
      }
      error={erro}
      loading={enviando}
      footer={
        confirmando ? (
          <>
            <Button variant="secondary" onClick={() => setConfirmando(false)} disabled={enviando}>
              Voltar
            </Button>
            <Button loading={enviando} onClick={() => motivo && onCancelar(motivo, texto.trim())}>
              Cancelar comanda
            </Button>
          </>
        ) : (
          <>
            <Button variant="secondary" onClick={() => onOpenChange(false)}>
              Voltar
            </Button>
            <Button disabled={!valido || bloqueio !== null} onClick={() => setConfirmando(true)}>
              Continuar
            </Button>
          </>
        )
      }
    >
      {bloqueio ? <Notice tone="info">{bloqueio}</Notice> : null}
      {confirmando ? null : (
        <>
          <div className="motivos" role="group" aria-label="Motivo do cancelamento">
            {MOTIVOS.map((m) => (
              <Chip key={m.codigo} selected={motivo === m.codigo} onClick={() => setMotivo(m.codigo)}>
                {m.rotulo}
              </Chip>
            ))}
          </div>
          {motivo === "outro" ? (
            <TextField
              label="Qual o motivo?"
              value={texto}
              maxLength={200}
              autoComplete="off"
              onChange={(e) => setTexto(e.target.value)}
            />
          ) : null}
        </>
      )}
    </BottomSheet>
  );
}
