export type IconName =
  | "search"
  | "list"
  | "grid"
  | "wallet"
  | "book"
  | "users"
  | "plus"
  | "sun"
  | "moon"
  | "warn"
  | "clock"
  | "dot"
  | "check"
  | "logout";

/** Stroke paths on a 24x24 grid, drawn once here so a screen never ships its own SVG. */
const PATHS: Record<IconName, string[]> = {
  search: ["M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14z", "M21 21l-4.3-4.3"],
  list: ["M8 6h13", "M8 12h13", "M8 18h13", "M3 6h.01", "M3 12h.01", "M3 18h.01"],
  grid: ["M4 4h7v7H4z", "M13 4h7v7h-7z", "M4 13h7v7H4z", "M13 13h7v7h-7z"],
  wallet: ["M3 7a2 2 0 0 1 2-2h13v4", "M3 7v11a2 2 0 0 0 2 2h15V9H5a2 2 0 0 1-2-2z", "M16 14.5h.01"],
  book: ["M4 5a2 2 0 0 1 2-2h14v16H6a2 2 0 0 0-2 2z", "M4 19v-14", "M8 7h8", "M8 11h6"],
  users: [
    "M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z",
    "M2 21a7 7 0 0 1 14 0",
    "M16 3.2a4 4 0 0 1 0 7.6",
    "M22 21a7 7 0 0 0-4-6.3",
  ],
  plus: ["M12 5v14", "M5 12h14"],
  sun: [
    "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z",
    "M12 2v2",
    "M12 20v2",
    "M4.9 4.9l1.4 1.4",
    "M17.7 17.7l1.4 1.4",
    "M2 12h2",
    "M20 12h2",
    "M4.9 19.1l1.4-1.4",
    "M17.7 6.3l1.4-1.4",
  ],
  moon: ["M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"],
  warn: ["M10.3 3.9L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z", "M12 9v4", "M12 17h.01"],
  clock: ["M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z", "M12 7v5l3 2"],
  dot: ["M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z"],
  check: ["M20 6L9 17l-5-5"],
  logout: ["M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4", "M16 17l5-5-5-5", "M21 12H9"],
};

export interface IconProps {
  name: IconName;
  size?: number;
}

/** A decorative icon (`aria-hidden`): the word next to it carries the meaning, an icon never stands alone as the only sign. */
export function Icon({ name, size = 20 }: IconProps) {
  return (
    <svg
      className="f4-icon"
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill={name === "dot" ? "currentColor" : "none"}
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {PATHS[name].map((d) => (
        <path key={d} d={d} />
      ))}
    </svg>
  );
}
