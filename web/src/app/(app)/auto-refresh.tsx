"use client";

import { useRouter } from "next/navigation";
import { useEffect, useRef } from "react";

/**
 * The balance is eventually consistent (an entry shows up a moment after it is saved), so the page asks the server
 * for fresh data every few seconds. Only the data is refetched; the page is not reloaded. The LED beats on each
 * refresh, so the person sees the screen is alive.
 */
export function AutoRefresh({ seconds }: Readonly<{ seconds: number }>) {
  const router = useRouter();
  const led = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    const id = setInterval(() => {
      router.refresh();
      const el = led.current;
      if (el) {
        el.classList.remove("beat");
        void el.offsetWidth; // restart the animation
        el.classList.add("beat");
      }
    }, seconds * 1000);

    return () => clearInterval(id);
  }, [router, seconds]);

  return (
    <span className="chip" title="A tela busca dados novos automaticamente">
      <span className="led" ref={led} />
      ao vivo · atualiza a cada {seconds} s
    </span>
  );
}
