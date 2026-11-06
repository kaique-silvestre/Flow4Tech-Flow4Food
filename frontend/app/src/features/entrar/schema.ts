import { z } from "zod";

/** An identifier with an at-sign is an e-mail; anything else is an operational user name (and then the organization is required). */
export function ehEmail(identificador: string): boolean {
  return identificador.includes("@");
}

export const entrarSchema = z
  .object({
    identificador: z.string().trim().min(1, "Informe o usuário ou e-mail."),
    senha: z.string().min(1, "Informe a senha."),
    organizacao: z.string().trim(),
  })
  .superRefine((valor, contexto) => {
    if (!ehEmail(valor.identificador) && valor.identificador !== "" && valor.organizacao === "") {
      contexto.addIssue({ code: "custom", path: ["organizacao"], message: "Informe o código da organização." });
    }
  });

export type EntrarValores = z.infer<typeof entrarSchema>;

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The organization code is the id the owner hands over; a malformed one is caught here, but never told apart from a wrong user or password by the server. */
export function organizacaoValida(valor: string): boolean {
  return UUID.test(valor.trim());
}
