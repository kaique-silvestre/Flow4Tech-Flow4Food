import { describe, expect, it } from "vitest";
import { centsFromDigits, formatBRL } from "./format";

describe("formatBRL", () => {
  it.each([
    [0, "R$ 0,00"],
    [5, "R$ 0,05"],
    [100, "R$ 1,00"],
    [123456, "R$ 1.234,56"],
    [100_000_000, "R$ 1.000.000,00"],
    [-2750, "-R$ 27,50"],
  ])("formats %i cents as %s", (cents, text) => {
    expect(formatBRL(cents)).toBe(text);
  });

  it("refuses a fractional or unsafe amount instead of rounding silently", () => {
    expect(() => formatBRL(10.5)).toThrow(RangeError);
    expect(() => formatBRL(Number.NaN)).toThrow(RangeError);
  });
});

describe("centsFromDigits", () => {
  it("reads the keypad digits as cents and an empty entry as zero", () => {
    expect(centsFromDigits("")).toBe(0);
    expect(centsFromDigits("1250")).toBe(1250);
    expect(() => centsFromDigits("12a")).toThrow(RangeError);
  });
});
