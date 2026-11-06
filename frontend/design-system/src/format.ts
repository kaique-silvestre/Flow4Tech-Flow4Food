/** Amounts are integer cents (BRL). Always shown as `R$ 1.234,56`, with a plain (breaking) space so a price never wraps in two. */
export function formatBRL(centavos: number): string {
  if (!Number.isSafeInteger(centavos)) {
    throw new RangeError("formatBRL expects integer cents");
  }
  const negative = centavos < 0;
  const abs = Math.abs(centavos);
  const reais = Math.trunc(abs / 100)
    .toString()
    .replace(/\B(?=(\d{3})+(?!\d))/g, ".");
  const cents = (abs % 100).toString().padStart(2, "0");
  return `${negative ? "-" : ""}R$ ${reais},${cents}`;
}

/** Parses what the keypad collected (digits only, as cents) into an integer number of cents; empty means 0. */
export function centsFromDigits(digits: string): number {
  if (!/^\d*$/.test(digits)) {
    throw new RangeError("digits only");
  }
  return digits === "" ? 0 : Number.parseInt(digits, 10);
}
