import { BottomSheet, Button, Chip, Keypad, TextField } from "@flow4tech/design-system";
import { useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useEffect, useState } from "react";
import { api } from "../sessao/sessao";
import { paraComanda, acharMesaPorNumero, type Comanda, type Mesa } from "./tipos";

const MAXIMO_DIGITOS = 3;
const CHIPS_VISIVEIS = 6;

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  mesas: readonly Mesa[];
  comandas: readonly Comanda[];
}

type Etapa = "teclado" | "nome" | "ocupada";

/**
 * New comanda by keypad (docs/design/refs/salao/04.png): a huge entry, free-table chips when the unit uses tables, 12 keys, "Dar um nome" and an OK that stays off until there is a value.
 * With tables the entry names a table ("Mesa 12"); without them it names the comanda ("Comanda 7"). An occupied table asks before opening another comanda on it.
 */
export function NovaComandaSheet({ open, onOpenChange, mesas, comandas }: Props) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const usaMesas = mesas.length > 0;
  const [etapa, setEtapa] = useState<Etapa>("teclado");
  const [digitos, setDigitos] = useState("");
  const [mesaEscolhida, setMesaEscolhida] = useState<string | null>(null);
  const [nome, setNome] = useState("");
  const [enviando, setEnviando] = useState(false);
  const [erro, setErro] = useState<string | undefined>(undefined);

  useEffect(() => {
    if (open) {
      setEtapa("teclado");
      setDigitos("");
      setMesaEscolhida(null);
      setNome("");
      setErro(undefined);
      setEnviando(false);
    }
  }, [open]);

  const livres = mesas.filter((m) => m.status === "livre").slice(0, CHIPS_VISIVEIS);
  const mesaPorChip = mesas.find((m) => m.id === mesaEscolhida);
  const mesaDigitada = usaMesas && digitos !== "" ? acharMesaPorNumero(mesas, digitos) : undefined;
  const mesaAlvo = mesaPorChip ?? mesaDigitada;
  const rotulo = usaMesas ? "Mesa" : "Comanda";
  const entrada = mesaPorChip ? mesaPorChip.identificacao : digitos;
  const podeConfirmar = etapa === "nome" ? nome.trim() !== "" : entrada !== "";

  async function abrir(corpo: { identificacao?: string; mesaId?: string }) {
    setErro(undefined);
    setEnviando(true);
    try {
      const { data, response } = await api.POST("/comandas", { body: { identificacao: corpo.identificacao ?? null, mesaId: corpo.mesaId ?? null, pessoas: null } });
      if (data) {
        const comanda = paraComanda(data as unknown as Record<string, unknown>);
        await Promise.all([queryClient.invalidateQueries({ queryKey: ["comandas"] }), queryClient.invalidateQueries({ queryKey: ["mesas"] })]);
        onOpenChange(false);
        await navigate({ to: "/salao/comanda/$comandaId", params: { comandaId: comanda.id } });
      } else if (response.status === 409) {
        setErro("Esta unidade não usa mesas. Atualize a tela.");
        await queryClient.invalidateQueries({ queryKey: ["mesas"] });
      } else if (response.status === 403) {
        setErro("Você não tem permissão para abrir comandas.");
      } else {
        setErro("Não foi possível abrir. Confira o número e tente de novo.");
      }
    } catch {
      setErro("Sem conexão. Tente de novo.");
    } finally {
      setEnviando(false);
    }
  }

  async function confirmar(jaConfirmouOcupada = false) {
    if (etapa === "nome") {
      await abrir({ identificacao: nome.trim() });
      return;
    }

    if (!usaMesas) {
      await abrir({ identificacao: `Comanda ${digitos}` });
      return;
    }

    if (!mesaAlvo) {
      setErro(`Não existe a mesa ${digitos}.`);
      return;
    }

    if (mesaAlvo.status === "ocupada" && !jaConfirmouOcupada) {
      setEtapa("ocupada");
      return;
    }

    await abrir({ mesaId: mesaAlvo.id });
  }

  const existentes = mesaAlvo ? comandas.filter((c) => c.mesaId === mesaAlvo.id) : [];

  if (etapa === "ocupada" && mesaAlvo) {
    return (
      <BottomSheet
        open={open}
        onOpenChange={onOpenChange}
        title={`Mesa ${mesaAlvo.identificacao} já está ocupada`}
        description={existentes.length > 0 ? "Já existe comanda aberta nesta mesa. Quer abrir mais uma?" : "A mesa tem uma comanda aberta (de outra pessoa). Quer abrir mais uma?"}
        error={erro}
        loading={enviando}
        footer={
          <>
            <Button variant="secondary" onClick={() => setEtapa("teclado")} disabled={enviando}>
              Voltar
            </Button>
            <Button loading={enviando} onClick={() => void confirmar(true)}>
              Abrir outra comanda
            </Button>
          </>
        }
      >
        <ul className="ocupada-lista" aria-label="Comandas já abertas nesta mesa">
          {existentes.map((c) => (
            <li key={c.id}>
              <Button
                variant="secondary"
                className="ocupada-item"
                onClick={() => {
                  onOpenChange(false);
                  void navigate({ to: "/salao/comanda/$comandaId", params: { comandaId: c.id } });
                }}
              >
                {c.numeroTexto} · {c.identificacao}
              </Button>
            </li>
          ))}
        </ul>
      </BottomSheet>
    );
  }

  if (etapa === "nome") {
    return (
      <BottomSheet
        open={open}
        onOpenChange={onOpenChange}
        title="Dar um nome"
        description="Por exemplo, o nome de quem pediu."
        error={erro}
        loading={enviando}
        footer={
          <>
            <Button variant="secondary" onClick={() => setEtapa("teclado")} disabled={enviando}>
              Voltar
            </Button>
            <Button loading={enviando} disabled={!podeConfirmar} onClick={() => void confirmar()}>
              OK
            </Button>
          </>
        }
      >
        <TextField label="Nome da comanda" value={nome} maxLength={40} autoFocus autoComplete="off" onChange={(e) => setNome(e.target.value)} />
      </BottomSheet>
    );
  }

  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Nova comanda"
      description={usaMesas ? "Digite o número da mesa." : "Digite o número da comanda."}
      error={erro}
      loading={enviando}
      footer={
        <>
          <Button variant="secondary" onClick={() => setEtapa("nome")} disabled={enviando}>
            Dar um nome
          </Button>
          <Button loading={enviando} disabled={!podeConfirmar} onClick={() => void confirmar()}>
            OK
          </Button>
        </>
      }
    >
      <p className="nova-entrada f4-num" aria-live="polite" aria-label={`${rotulo} ${entrada === "" ? "em branco" : entrada}`}>
        <span className="nova-rotulo">{rotulo}</span> {entrada === "" ? "—" : entrada}
      </p>
      {usaMesas && livres.length > 0 ? (
        <div className="nova-chips" role="group" aria-label="Mesas livres">
          {livres.map((mesa) => (
            <Chip
              key={mesa.id}
              selected={mesaEscolhida === mesa.id}
              onClick={() => {
                setMesaEscolhida((atual) => (atual === mesa.id ? null : mesa.id));
                setDigitos("");
                setErro(undefined);
              }}
            >
              {mesa.identificacao}
            </Chip>
          ))}
        </div>
      ) : null}
      <Keypad
        disabled={enviando}
        onDigit={(d) => {
          setErro(undefined);
          setMesaEscolhida(null);
          setDigitos((atual) => (atual.length >= MAXIMO_DIGITOS ? atual : atual === "" && d === "0" ? atual : atual + d));
        }}
        onBackspace={() => {
          setMesaEscolhida(null);
          setDigitos((atual) => atual.slice(0, -1));
        }}
        onCancel={() => onOpenChange(false)}
      />
    </BottomSheet>
  );
}
