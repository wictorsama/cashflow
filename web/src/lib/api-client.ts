/** Typed client of the Cash Flow API (through the gateway). It takes the token as a parameter, so it is easy to test. */

export type EntryType = "Credit" | "Debit";

export interface Entry {
  id: string;
  merchantId: string;
  type: EntryType;
  amount: number;
  entryDate: string;
  description: string | null;
  /** Who registered it (the subject of the token): a person or an integration. "unknown" for entries from before this was recorded. */
  createdBy: string;
  registeredAt: string;
  reversalOf: string | null;
  reversedBy: string | null;
}

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface DailyBalance {
  merchantId: string;
  date: string;
  credits: number;
  debits: number;
  dayResult: number;
  accumulatedBalance: number;
}

export interface BalanceRange {
  merchantId: string;
  from: string;
  to: string;
  openingBalance: number;
  closingBalance: number;
  days: DailyBalance[];
}

/** What the assistant proposes for a sentence. It is only a proposal: nothing is recorded until the person confirms. */
export interface EntryDraft {
  type: EntryType;
  amount: number;
  entryDate: string;
  description: string | null;
}

export type DraftResult =
  | { status: "draft"; draft: EntryDraft; provider: string }
  | { status: "needs_clarification"; question: string; provider: string }
  | { status: "rejected"; reason: string; provider: string };

export interface RegisterEntryInput {
  type: EntryType;
  amount: number;
  entryDate: string;
  description?: string;
}

/** A failure answered by the API (RFC 9457 problem details) or a network failure (status 0). */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly title: string,
    readonly detail: string,
  ) {
    super(`${status} ${title}: ${detail}`);
    this.name = "ApiError";
  }
}

const merchantQuery = (merchantId: string | undefined, prefix = "?") =>
  merchantId ? `${prefix}merchantId=${encodeURIComponent(merchantId)}` : "";

export class CashFlowApi {
  constructor(
    private readonly baseUrl: string,
    private readonly token: string,
    private readonly fetcher: typeof fetch = fetch,
  ) {}

  // "merchantId" is only for an auditor, who must name the merchant it reads. A merchant never sends it: the API
  // takes the merchant from the token and refuses a different one.
  getBalance(date: string, merchantId?: string) {
    return this.send<DailyBalance>("GET", `/v1/balances/${date}${merchantQuery(merchantId)}`);
  }

  getBalanceRange(from: string, to: string, merchantId?: string) {
    return this.send<BalanceRange>("GET", `/v1/balances?from=${from}&to=${to}${merchantQuery(merchantId, "&")}`);
  }

  listEntries(query: { from: string; to: string; page?: number; pageSize?: number; merchantId?: string }) {
    const params = new URLSearchParams({ from: query.from, to: query.to });
    params.set("page", String(query.page ?? 1));
    params.set("pageSize", String(query.pageSize ?? 20));
    if (query.merchantId) params.set("merchantId", query.merchantId);

    return this.send<Paged<Entry>>("GET", `/v1/entries?${params}`);
  }

  /** Asks the assistant to turn a sentence into a draft. It never records anything. */
  draftEntry(text: string) {
    return this.send<DraftResult>("POST", "/v1/assistant/entry-drafts", { text });
  }

  /** The idempotency key makes a double click or a retry register the entry only once. */
  registerEntry(input: RegisterEntryInput, idempotencyKey: string) {
    return this.send<Entry>("POST", "/v1/entries", input, idempotencyKey);
  }

  reverseEntry(id: string, reason: string | undefined, idempotencyKey: string) {
    return this.send<Entry>("POST", `/v1/entries/${id}/reversals`, { reason }, idempotencyKey);
  }

  private async send<T>(method: string, path: string, body?: unknown, idempotencyKey?: string): Promise<T> {
    const headers: Record<string, string> = { Authorization: `Bearer ${this.token}`, Accept: "application/json" };
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;

    let response: Response;
    try {
      response = await this.fetcher(`${this.baseUrl}${path}`, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        cache: "no-store",
        signal: AbortSignal.timeout(10_000),
      });
    } catch {
      throw new ApiError(0, "Serviço indisponível", "Não foi possível falar com o serviço. Tente novamente em instantes.");
    }

    if (!response.ok) {
      const problem = await response.json().catch(() => ({}) as { title?: string; detail?: string });
      throw new ApiError(response.status, problem.title ?? response.statusText, problem.detail ?? "");
    }

    return (await response.json()) as T;
  }
}
