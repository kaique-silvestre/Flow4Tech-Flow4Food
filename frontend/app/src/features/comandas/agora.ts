import { useEffect, useState } from "react";

/** The clock of the lists: "now" in ms, renewed every 30 s so the times open and the stalled alerts keep up without a request. */
export function useAgora(intervaloMs = 30_000) {
  const [agora, setAgora] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setAgora(Date.now()), intervaloMs);
    return () => window.clearInterval(timer);
  }, [intervaloMs]);
  return agora;
}
