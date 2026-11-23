/** The CashFlow mark: a coin-shaped "C" (the cash) crossed by a wave (the flow). Colours come from the theme tokens. */
export function LogoMark({ className }: Readonly<{ className?: string }>) {
  return (
    <svg className={className} viewBox="0 0 48 48" aria-hidden="true" focusable="false">
      <path d="M 36.26 13.72 A 16 16 0 1 0 36.26 34.28" fill="none" stroke="var(--ink)" strokeWidth="5" strokeLinecap="round" />
      <path d="M 13 24 C 17 15, 22 15, 26 24 S 35 33, 42 22" fill="none" stroke="var(--acc)" strokeWidth="4" strokeLinecap="round" />
    </svg>
  );
}

export function Wordmark() {
  return (
    <>
      cash<b>flow</b>_
    </>
  );
}
