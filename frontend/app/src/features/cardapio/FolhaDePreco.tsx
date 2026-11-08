import { BottomSheet, Button, formatBRL, MoneyField, Notice, TextField } from "@flow4tech/design-system";
import { useState } from "react";
import type { Resultado } from "./CardapioPage";
import {
  deInputLocal,
  type ErrosDePreco,
  lerDataHoraBR,
  type Mudanca,
  mascararDataHora,
  type ProdutoGestao,
  paraInputLocal,
  paraTextoBR,
  validarPreco,
} from "./logica";

interface Props {
  produto: ProdutoGestao | null;
  onFechar: () => void;
  onSalvar: (produto: ProdutoGestao, mudanca: Mudanca) => Promise<Resultado>;
}

/** The sheet opens for one product; the key makes the form start fresh from that product's values every time. */
export function FolhaDePreco({ produto, onFechar, onSalvar }: Props) {
  return (
    <BottomSheet
      open={produto !== null}
      onOpenChange={(aberta) => {
        if (!aberta) {
          onFechar();
        }
      }}
      title={produto ? `Preço de ${produto.nome}` : "Preço"}
      description="O preço novo vale para os próximos lançamentos. O que já foi lançado não muda."
    >
      {produto ? <Formulario key={produto.id} produto={produto} onFechar={onFechar} onSalvar={onSalvar} /> : null}
    </BottomSheet>
  );
}

function Formulario({
  produto,
  onFechar,
  onSalvar,
}: {
  produto: ProdutoGestao;
  onFechar: () => void;
  onSalvar: Props["onSalvar"];
}) {
  const [base, setBase] = useState(String(produto.precoCentavos));
  const [promo, setPromo] = useState(produto.promocao ? String(produto.promocao.precoCentavos) : "");
  const [inicio, setInicio] = useState(produto.promocao ? paraTextoBR(paraInputLocal(produto.promocao.inicio)) : "");
  const [fim, setFim] = useState(produto.promocao ? paraTextoBR(paraInputLocal(produto.promocao.fim)) : "");
  const [erros, setErros] = useState<ErrosDePreco>({});
  const [salvando, setSalvando] = useState(false);
  const [falha, setFalha] = useState<string | null>(null);

  async function salvar() {
    setFalha(null);
    const inicioLido = lerDataHoraBR(inicio);
    const fimLido = lerDataHoraBR(fim);
    const achados = validarPreco({ base, promo, inicio: inicioLido ?? "", fim: fimLido ?? "" });
    const formato = "Use dia/mês/ano e hora, assim: 10/11/2026 18:00.";
    if (promo !== "" && Number(promo) > 0) {
      if (inicioLido === null) {
        achados.inicio = formato;
      }

      if (fimLido === null) {
        achados.fim = formato;
      }
    }

    setErros(achados);
    if (Object.keys(achados).length > 0) {
      return;
    }

    const comPromocao = promo !== "" && Number(promo) > 0;
    const novaPromocao = comPromocao
      ? { precoCentavos: Number(promo), inicio: deInputLocal(inicioLido ?? ""), fim: deInputLocal(fimLido ?? "") }
      : null;
    setSalvando(true);
    const r = await onSalvar(produto, { precoCentavos: Number(base), promocao: novaPromocao });
    setSalvando(false);
    if (r.ok) {
      onFechar();
    } else {
      setFalha(r.mensagem);
    }
  }

  return (
    <>
      {produto.precoDaUnidadeCentavos !== null ? (
        <Notice tone="info">
          {`Esta unidade tem preço próprio de ${formatBRL(produto.precoDaUnidadeCentavos)}. Ele vale no lugar do preço normal e da promoção.`}
        </Notice>
      ) : null}
      <MoneyField label="Preço normal" digits={base} onDigitsChange={setBase} error={erros.base} disabled={salvando} />
      <MoneyField
        label="Preço promocional"
        hint="Deixe em branco para não ter promoção."
        digits={promo}
        onDigitsChange={setPromo}
        error={erros.promo}
        disabled={salvando}
      />
      <TextField
        label="Promoção começa"
        inputMode="numeric"
        autoComplete="off"
        placeholder="dd/mm/aaaa hh:mm"
        value={inicio}
        onChange={(e) => setInicio(mascararDataHora(e.target.value))}
        error={erros.inicio}
        disabled={salvando}
      />
      <TextField
        label="Promoção termina"
        inputMode="numeric"
        autoComplete="off"
        placeholder="dd/mm/aaaa hh:mm"
        value={fim}
        onChange={(e) => setFim(mascararDataHora(e.target.value))}
        error={erros.fim}
        disabled={salvando}
      />
      {falha ? (
        <p role="alert" className="f4-field-error">
          {falha}
        </p>
      ) : null}
      <div className="f4-sheet-footer">
        <Button variant="secondary" onClick={onFechar} disabled={salvando}>
          Cancelar
        </Button>
        <Button loading={salvando} onClick={() => void salvar()}>
          Salvar preço
        </Button>
      </div>
    </>
  );
}
