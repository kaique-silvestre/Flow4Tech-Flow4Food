import { Button, Notice } from "@flow4tech/design-system";

const CARREGAMENTO = /dynamically imported module|importing a module script|unable to preload|loading chunk/i;

/** Whether the error is the browser failing to download the code of a screen (offline, flaky network, or a deploy that replaced the file). */
export function erroDeCarregamento(erro: unknown): boolean {
  return erro instanceof Error && CARREGAMENTO.test(erro.message);
}

/**
 * The error screen of every route. Screens are downloaded on demand: one already opened works offline (the service worker keeps every chunk),
 * one never opened and without connection ends here, in words, with the tab bar still in place.
 */
export function ErroDeRota({ error, reset }: { error: unknown; reset: () => void }) {
  return (
    <Notice
      tone="error"
      action={
        <Button variant="secondary" onClick={reset}>
          Tentar novamente
        </Button>
      }
    >
      {erroDeCarregamento(error)
        ? "Sem conexão para abrir esta tela. Conecte-se e tente de novo; as telas já abertas continuam funcionando."
        : "Não foi possível abrir esta tela."}
    </Notice>
  );
}
