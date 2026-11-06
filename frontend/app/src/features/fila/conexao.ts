import { useEffect, useState } from "react";

/** Whether the browser believes it has a connection. It only says "no" when the device knows it is offline; a request that fails anyway is handled where it is made. */
export function useOnline(): boolean {
  const [online, setOnline] = useState(() => navigator.onLine);
  useEffect(() => {
    const ligar = () => setOnline(true);
    const desligar = () => setOnline(false);
    window.addEventListener("online", ligar);
    window.addEventListener("offline", desligar);
    return () => {
      window.removeEventListener("online", ligar);
      window.removeEventListener("offline", desligar);
    };
  }, []);
  return online;
}
