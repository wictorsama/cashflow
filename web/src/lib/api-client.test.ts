import { describe, expect, it, vi } from "vitest";
import { ApiError, CashFlowApi } from "./api-client";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

function apiWith(response: Response | Error) {
  const fetcher = vi.fn(async () => {
    if (response instanceof Error) throw response;

    return response;
  });

  return { api: new CashFlowApi("http://gateway", "tok", fetcher as unknown as typeof fetch), fetcher };
}

const lastCall = (fetcher: ReturnType<typeof vi.fn>) => {
  const [url, init] = fetcher.mock.calls[0] as unknown as [string, RequestInit];

  return { url, init, headers: init.headers as Record<string, string> };
};

describe("CashFlowApi", () => {
  it("sends the bearer token and never a merchant header", async () => {
    const { api, fetcher } = apiWith(json({ credits: 1 }));

    await api.getBalance("2026-11-17");

    const { url, headers } = lastCall(fetcher);
    expect(url).toBe("http://gateway/v1/balances/2026-11-17");
    expect(headers.Authorization).toBe("Bearer tok");
    expect(Object.keys(headers).map((h) => h.toLowerCase())).not.toContain("x-merchant-id");
  });

  it("sends the idempotency key and the body when registering an entry", async () => {
    const { api, fetcher } = apiWith(json({ id: "1" }, 201));

    await api.registerEntry({ type: "Credit", amount: 10, entryDate: "2026-11-17" }, "key-1");

    const { url, init, headers } = lastCall(fetcher);
    expect(url).toBe("http://gateway/v1/entries");
    expect(init.method).toBe("POST");
    expect(headers["Idempotency-Key"]).toBe("key-1");
    expect(JSON.parse(String(init.body))).toEqual({ type: "Credit", amount: 10, entryDate: "2026-11-17" });
  });

  it("builds the entries query with paging", async () => {
    const { api, fetcher } = apiWith(json({ items: [], page: 2, pageSize: 20, totalCount: 0 }));

    await api.listEntries({ from: "2026-11-01", to: "2026-11-17", page: 2 });

    expect(lastCall(fetcher).url).toBe("http://gateway/v1/entries?from=2026-11-01&to=2026-11-17&page=2&pageSize=20");
  });

  it("turns a problem-details answer into an ApiError", async () => {
    const { api } = apiWith(json({ title: "Business rule violated", detail: "Amount must be positive." }, 422));

    await expect(api.getBalance("2026-11-17")).rejects.toMatchObject({
      status: 422,
      title: "Business rule violated",
      detail: "Amount must be positive.",
    });
  });

  it("copes with an error answer that is not JSON", async () => {
    const { api } = apiWith(new Response("<html>bad gateway</html>", { status: 502, statusText: "Bad Gateway" }));

    const error = await api.getBalance("2026-11-17").catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(502);
    expect((error as ApiError).title).toBe("Bad Gateway");
  });

  it("reports a network failure as status 0", async () => {
    const { api } = apiWith(new TypeError("fetch failed"));

    await expect(api.getBalance("2026-11-17")).rejects.toMatchObject({ status: 0 });
  });
});

/** A response body can be read once, so a test that makes several calls needs a new answer for each. */
function freshAnswers() {
  const fetcher = vi.fn(async () => json({}));

  return { api: new CashFlowApi("http://gateway", "tok", fetcher as unknown as typeof fetch), fetcher };
}

describe("CashFlowApi, reading as an auditor", () => {
  it("names the merchant on the balance of a day, of a range and on the entry list", async () => {
    const { api, fetcher } = freshAnswers();

    await api.getBalance("2026-11-17", "m-1");
    await api.getBalanceRange("2026-11-10", "2026-11-17", "m-1");
    await api.listEntries({ from: "2026-11-17", to: "2026-11-17", merchantId: "m-1" });

    const urls = fetcher.mock.calls.map((c) => (c as unknown as [string])[0]);
    expect(urls[0]).toBe("http://gateway/v1/balances/2026-11-17?merchantId=m-1");
    expect(urls[1]).toBe("http://gateway/v1/balances?from=2026-11-10&to=2026-11-17&merchantId=m-1");
    expect(urls[2]).toContain("merchantId=m-1");
  });

  it("leaves the merchant out when the caller is a merchant", async () => {
    const { api, fetcher } = freshAnswers();

    await api.getBalance("2026-11-17");
    await api.listEntries({ from: "2026-11-17", to: "2026-11-17" });

    const urls = fetcher.mock.calls.map((c) => (c as unknown as [string])[0]);
    expect(urls.some((u) => u.includes("merchantId"))).toBe(false);
  });

  it("escapes the merchant so it cannot inject another parameter", async () => {
    const { api, fetcher } = apiWith(json({}));

    await api.getBalance("2026-11-17", "x&from=1");

    expect(lastCall(fetcher).url).toBe("http://gateway/v1/balances/2026-11-17?merchantId=x%26from%3D1");
  });
});

describe("CashFlowApi, the assistant", () => {
  it("sends only the sentence to the assistant route and reads the draft", async () => {
    const draft = { status: "draft", draft: { type: "Debit", amount: 150, entryDate: "2026-11-16", description: "Luz" }, provider: "rules" };
    const { api, fetcher } = apiWith(json(draft));

    const result = await api.draftEntry("paguei 150 de luz ontem");

    const { url, init, headers } = lastCall(fetcher);
    expect(url).toBe("http://gateway/v1/assistant/entry-drafts");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({ text: "paguei 150 de luz ontem" }); // no merchant, no history
    expect(headers.Authorization).toBe("Bearer tok");
    expect(result).toEqual(draft);
  });

  it("does not send an idempotency key: a draft records nothing", async () => {
    const { api, fetcher } = apiWith(json({ status: "rejected", reason: "x", provider: "rules" }));

    await api.draftEntry("bom dia");

    expect(Object.keys(lastCall(fetcher).headers).map((h) => h.toLowerCase())).not.toContain("idempotency-key");
  });

  it("turns an unavailable assistant into an ApiError the screen can explain", async () => {
    const { api } = apiWith(json({ title: "Assistant unavailable", detail: "Use the form." }, 503));

    await expect(api.draftEntry("paguei 10")).rejects.toMatchObject({ status: 503 });
  });
});
