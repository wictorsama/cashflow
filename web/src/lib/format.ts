const SAO_PAULO_OFFSET_HOURS = -3;

const brl = new Intl.NumberFormat("pt-BR", { style: "currency", currency: "BRL" });

export const formatMoney = (value: number): string => brl.format(value);

/** The merchant's day is the Sao Paulo day, the same rule the API applies. Returns yyyy-mm-dd. */
export function todayInSaoPaulo(now: Date = new Date()): string {
  return new Date(now.getTime() + SAO_PAULO_OFFSET_HOURS * 3600 * 1000).toISOString().slice(0, 10);
}

export function addDays(isoDate: string, days: number): string {
  const date = new Date(`${isoDate}T00:00:00Z`);
  date.setUTCDate(date.getUTCDate() + days);

  return date.toISOString().slice(0, 10);
}

export const isIsoDate = (value: string | undefined): value is string =>
  value !== undefined && /^\d{4}-\d{2}-\d{2}$/.test(value) && !Number.isNaN(Date.parse(`${value}T00:00:00Z`));

/** The time of day (hh:mm) in the merchant's timezone, for a moment such as an entry's registration time. */
export function formatTime(isoInstant: string): string {
  return new Date(isoInstant).toLocaleTimeString("pt-BR", { timeZone: "America/Sao_Paulo", hour: "2-digit", minute: "2-digit" });
}

export function formatDate(isoDate: string): string {
  const [year, month, day] = isoDate.split("-");

  return `${day}/${month}/${year}`;
}

/**
 * Reads what a person types in an amount field: "1.234,56", "1234,56" or "1234.56".
 * Returns null for anything that is not a positive amount with at most two decimals.
 */
export function parseAmount(input: string): number | null {
  const text = input.trim().replace(/\s/g, "");
  const normalized = text.includes(",") ? text.replace(/\./g, "").replace(",", ".") : text;

  if (!/^\d+(\.\d{1,2})?$/.test(normalized)) return null;
  const value = Number(normalized);

  return value > 0 ? value : null;
}
