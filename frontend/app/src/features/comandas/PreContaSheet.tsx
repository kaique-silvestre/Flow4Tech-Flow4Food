import { BottomSheet, Button, Notice, Skeleton } from "@flow4tech/design-system";
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useOnline } from "../fila/conexao";
import { CorpoDoComprovante, useCompartilhar } from "./Comprovante";
import { falhaDe, horaCurta, pedirConta, textoDaFalha, useComprovante } from "./comprovante";

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  comandaId: string;
  nome: string;
  /** Whether the caller may mark the bill as requested (comanda:ver). */
  podePedir: boolean;
  /** The server already marked this comanda (from the list). */
  contaPedidaEm: string | null;
}

/** The pre-bill with the server's numbers, to show or share, and the "Conta pedida" mark that puts the table in "aguardando conta". Online only: the numbers and the mark come from the server. */
export function PreContaSheet({ open, onOpenChange, comandaId, nome, podePedir, contaPedidaEm }: Props) {
  const queryClient = useQueryClient();
  const online = useOnline();
  const preConta = useComprovante("pre-conta", comandaId, open);
  const { ocupado, aviso, erro, compartilhar } = useCompartilhar("pre-conta", comandaId);
  const [pedindo, setPedindo] = useState(false);
  const [pedida, setPedida] = useState<string | null>(null);
  const [erroPedir, setErroPedir] = useState<string | undefined>(undefined);

  const marca = pedida ?? preConta.data?.contaPedidaEm ?? contaPedidaEm;

  async function pedir() {
    setPedindo(true);
    setErroPedir(undefined);
    try {
      const r = await pedirConta(comandaId);
      setPedida(r.contaPedidaEm ?? new Date().toISOString());
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["comandas"] }),
        queryClient.invalidateQueries({ queryKey: ["mesas"] }),
      ]);
    } catch (e) {
      const falha = falhaDe(e);
      setErroPedir(
        falha === "proibido"
          ? "Você não tem permissão para pedir a conta."
          : falha === "estado"
            ? "Esta comanda já não está aberta."
            : falha === "nao-encontrada"
              ? "Comanda não encontrada."
              : "Não foi possível pedir a conta. Tente de novo.",
      );
    } finally {
      setPedindo(false);
    }
  }

  const falha = preConta.isError ? falhaDe(preConta.error) : null;
  return (
    <BottomSheet
      open={open}
      onOpenChange={onOpenChange}
      title="Pré-conta"
      description={nome}
      loading={pedindo || ocupado}
      error={erroPedir ?? erro}
      footer={
        <div className="comp-rodape">
          {!online ? (
            <Notice tone="info">Sem conexão. Pedir a conta e compartilhar precisam do servidor.</Notice>
          ) : null}
          {aviso ? (
            <p role="status" className="comp-status">
              {aviso}
            </p>
          ) : null}
          <div className="comp-botoes">
            <Button variant="secondary" onClick={() => onOpenChange(false)}>
              Voltar
            </Button>
            <Button
              variant="secondary"
              loading={ocupado}
              disabled={!online || !preConta.isSuccess}
              onClick={() => void compartilhar()}
            >
              Compartilhar
            </Button>
            {podePedir ? (
              marca ? (
                <Button disabled aria-disabled="true">
                  {`Conta pedida${horaCurta(marca) ? ` às ${horaCurta(marca)}` : ""}`}
                </Button>
              ) : (
                <Button loading={pedindo} disabled={!online || !preConta.isSuccess} onClick={() => void pedir()}>
                  Pedir a conta
                </Button>
              )
            ) : null}
          </div>
        </div>
      }
    >
      {preConta.isPending ? <Skeleton rows={4} label="Carregando a pré-conta" /> : null}
      {falha ? (
        <Notice
          tone="error"
          action={
            falha === "rede" || falha === "outro" ? (
              <Button variant="secondary" onClick={() => void preConta.refetch()}>
                Tentar novamente
              </Button>
            ) : undefined
          }
        >
          {textoDaFalha(falha, "pre-conta")}
        </Notice>
      ) : null}
      {preConta.data ? <CorpoDoComprovante c={preConta.data} /> : null}
    </BottomSheet>
  );
}
