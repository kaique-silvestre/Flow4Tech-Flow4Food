import { BottomSheet, Button, Chip, formatBRL, Keypad, Stepper, TextField } from "@flow4tech/design-system";
import { useState } from "react";
import { type GrupoDeOpcao, type Opcao, type Produto, useOpcoesFrequentes } from "./cardapio";
import { alternar, grupoCheio, maisUsados, pendencias } from "./opcoes";
import {
  normalizarObservacao,
  OBSERVACAO_MAXIMA,
  type OpcaoEscolhida,
  precoUnitario,
  QUANTIDADE_MAXIMA,
} from "./rodada";

export interface EscolhaDoItem {
  opcoes: OpcaoEscolhida[];
  observacao: string | null;
  quantidade: number;
}

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  produto: Produto;
  grupos: readonly GrupoDeOpcao[];
  /** Editing a staged line: starts from what it has and says "Salvar". */
  inicial?: EscolhaDoItem | undefined;
  onConfirmar: (escolha: EscolhaDoItem) => void;
}

function rotuloDoGrupo(g: GrupoDeOpcao): string {
  const min = Math.min(g.minimo, g.opcoes.length);
  if (min >= 1) {
    return g.maximo === 1 ? "Obrigatório · escolha 1" : `Obrigatório · de ${min} a ${g.maximo}`;
  }

  return g.maximo === 1 ? "Opcional · escolha 1" : `Opcional · até ${g.maximo}`;
}

function PrecoDaOpcao({ opcao }: { opcao: Opcao }) {
  return opcao.precoCentavos > 0 ? (
    <span className="f4-num item-opcao-preco">+ {formatBRL(opcao.precoCentavos)}</span>
  ) : null;
}

/**
 * The item sheet: required groups to resolve, the most used add-ons as one-tap chips, the note and the quantity (steps or the numeric pad). It never sends anything; it hands the choice back to
 * the staged round. The state lives here, so the parent renders it with a `key` per product/line to start clean.
 * Mount it only while it is needed (the add-ons query runs while it is mounted and open).
 */
export function ItemSheet({ open, onOpenChange, produto, grupos, inicial, onConfirmar }: Props) {
  const [opcoes, setOpcoes] = useState<OpcaoEscolhida[]>(inicial?.opcoes ?? []);
  const [observacao, setObservacao] = useState(inicial?.observacao ?? "");
  const [quantidade, setQuantidade] = useState(inicial?.quantidade ?? 1);
  const [teclado, setTeclado] = useState(false);
  const [digitou, setDigitou] = useState(false);
  const frequentes = useOpcoesFrequentes(produto.id, open && grupos.length > 0);
  const usados = maisUsados(grupos, frequentes.data ?? []);
  const idsUsados = new Set(usados.map((u) => u.opcao.id));
  const faltas = pendencias(grupos, opcoes);
  const total = precoUnitario({ produto, opcoes }) * quantidade;
  const editando = inicial !== undefined;

  function digitar(d: string) {
    // The first digit after opening the pad replaces the number; the next ones extend it (at most 999).
    const atual = digitou ? String(quantidade) : "";
    const proximo = Number((atual + d).slice(0, 3));
    setQuantidade(Math.min(QUANTIDADE_MAXIMA, Math.max(1, proximo)));
    setDigitou(true);
  }

  function apagar() {
    const texto = String(quantidade);
    setQuantidade(texto.length <= 1 ? 1 : Number(texto.slice(0, -1)));
    setDigitou(true);
  }

  const antes = inicial?.quantidade ?? 1;
  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title={produto.nome}
      description={`${formatBRL(produto.precoCentavos)} a unidade${grupos.length > 0 ? ". Os adicionais somam ao preço." : "."}`}
      footer={
        <>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            Voltar
          </Button>
          <Button
            className="item-primario"
            disabled={faltas.length > 0}
            onClick={() => onConfirmar({ opcoes, observacao: normalizarObservacao(observacao), quantidade })}
          >
            {editando ? "Salvar" : "Adicionar"} · {formatBRL(total)}
          </Button>
        </>
      }
    >
      {grupos.length > 0 && frequentes.isLoading ? (
        <div className="item-esqueleto" role="status" aria-label="Carregando os mais usados">
          <span />
          <span />
          <span />
        </div>
      ) : null}
      {usados.length > 0 ? (
        <div className="item-usados" role="group" aria-label="Mais usados">
          <p className="item-subtitulo">Mais usados</p>
          <div className="item-chips">
            {usados.map(({ grupo, opcao }) => (
              <Chip
                key={opcao.id}
                selected={opcoes.some((o) => o.id === opcao.id)}
                disabled={!opcoes.some((o) => o.id === opcao.id) && grupoCheio(grupo, opcoes)}
                onClick={() => setOpcoes((atual) => alternar(atual, grupo, opcao))}
              >
                {opcao.nome}
                <PrecoDaOpcao opcao={opcao} />
              </Chip>
            ))}
          </div>
        </div>
      ) : null}

      {grupos.map((g) => {
        // An add-on already offered as a "mais usado" chip is not repeated in its group.
        const restantes = g.opcoes.filter((o) => !idsUsados.has(o.id));
        return (
          <div key={g.id} className="item-grupo" role="group" aria-label={g.nome}>
            <p className="item-subtitulo">
              {g.nome} <span className="item-regra">{rotuloDoGrupo(g)}</span>
            </p>
            {restantes.length === 0 ? (
              <p className="item-regra item-sem-resto">Escolha entre os mais usados, acima.</p>
            ) : (
              <div className="item-chips">
                {restantes.map((o) => {
                  const marcada = opcoes.some((e) => e.id === o.id);
                  return (
                    <Chip
                      key={o.id}
                      selected={marcada}
                      disabled={!marcada && grupoCheio(g, opcoes)}
                      onClick={() => setOpcoes((atual) => alternar(atual, g, o))}
                    >
                      {o.nome}
                      <PrecoDaOpcao opcao={o} />
                    </Chip>
                  );
                })}
              </div>
            )}
          </div>
        );
      })}

      <TextField
        label="Observação"
        hint="Ex.: sem gelo, ponto da carne. Vai junto com o item."
        value={observacao}
        maxLength={OBSERVACAO_MAXIMA}
        autoComplete="off"
        onChange={(e) => setObservacao(e.target.value)}
      />

      <div className="item-quantidade">
        <span className="item-subtitulo">Quantidade</span>
        <Stepper label="Quantidade" value={quantidade} min={1} onChange={setQuantidade} />
        <Button
          variant="secondary"
          aria-expanded={teclado}
          onClick={() => {
            setTeclado((aberto) => !aberto);
            setDigitou(false);
          }}
        >
          {teclado ? "Pronto" : "Teclado"}
        </Button>
      </div>
      {teclado ? (
        <div className="item-teclado">
          <Keypad
            onDigit={digitar}
            onBackspace={apagar}
            onCancel={() => {
              setQuantidade(antes);
              setTeclado(false);
            }}
          />
        </div>
      ) : null}

      {faltas.length > 0 ? (
        <ul className="item-faltas" aria-label="O que falta escolher">
          {faltas.map((f) => (
            <li key={f}>{f}</li>
          ))}
        </ul>
      ) : null}
    </BottomSheet>
  );
}
