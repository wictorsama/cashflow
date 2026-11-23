"use client";

import { useEffect, useRef } from "react";

const number = new Intl.NumberFormat("pt-BR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const show = (value: number) => number.format(value).replace("-", "−");

/**
 * Shows an amount and, when it appears or changes, counts up to it. The server already renders the final value, so
 * the number is right even before (or without) JavaScript. With "reduce motion" it just shows the value.
 */
export function CountUp({ value, ms = 1100 }: Readonly<{ value: number; ms?: number }>) {
  const ref = useRef<HTMLSpanElement>(null);
  const shown = useRef(0);

  useEffect(() => {
    const text = ref.current?.firstChild as Text | null | undefined;
    if (!text) return;

    const from = shown.current;
    shown.current = value;
    if (from === value || window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      text.data = show(value);
      return;
    }

    let frame = 0;
    const start = performance.now();
    const tick = (now: number) => {
      const progress = Math.min(1, (now - start) / ms);
      const eased = 1 - Math.pow(1 - progress, 4);
      text.data = show(from + (value - from) * eased);
      if (progress < 1) frame = requestAnimationFrame(tick);
    };
    frame = requestAnimationFrame(tick);

    return () => cancelAnimationFrame(frame);
  }, [value, ms]);

  return <span ref={ref}>{show(value)}</span>;
}
