"use server";

import { revalidatePath } from "next/cache";
import { ApiError } from "@/lib/api-client";
import { isIsoDate, parseAmount, todayInSaoPaulo } from "@/lib/format";
import { apiForCurrentUser } from "@/lib/gateway";

export interface DraftState {
  /** Changes with every answer, so the form notices a new draft even when it is the same as the last. */
  id: number;
  kind: "none" | "draft" | "question" | "refused" | "error";
  message: string;
  draft?: { type: "Credit" | "Debit"; amount: string; entryDate: string; description: string };
}

export interface FormState {
  ok: boolean;
  message: string;
}

const fail = (message: string): FormState => ({ ok: false, message });

function describe(error: unknown): FormState {
  if (!(error instanceof ApiError)) throw error;
  if (error.status === 0 || error.status >= 500) {
    return fail("Não foi possível concluir agora. Nada foi perdido: tente novamente em instantes.");
  }
  if (error.status === 429) return fail("Muitas requisições em pouco tempo. Aguarde um instante.");

  return fail(error.detail ? `${error.title}: ${error.detail}` : error.title);
}

const brl = new Intl.NumberFormat("pt-BR", { minimumFractionDigits: 2, maximumFractionDigits: 2, useGrouping: false });

/**
 * Asks the assistant for a draft. The result only fills the form for the person to review: nothing is recorded here,
 * and if the assistant is down or slow the form works exactly as before.
 */
export async function draftEntryAction(previous: DraftState, form: FormData): Promise<DraftState> {
  const id = previous.id + 1;
  const text = String(form.get("text") ?? "").trim();
  if (!text) return { id, kind: "error", message: "Escreva o lançamento com suas palavras, por exemplo: paguei 150 de luz ontem." };

  try {
    const api = await apiForCurrentUser();
    const result = await api.draftEntry(text);

    if (result.status === "draft") {
      return {
        id,
        kind: "draft",
        message: "Sugestão do assistente. Confira os campos abaixo: nada foi registrado até você clicar em Registrar.",
        draft: {
          type: result.draft.type,
          amount: brl.format(result.draft.amount),
          entryDate: result.draft.entryDate,
          description: result.draft.description ?? "",
        },
      };
    }
    if (result.status === "needs_clarification") return { id, kind: "question", message: result.question };

    return { id, kind: "refused", message: result.reason };
  } catch (error) {
    if (!(error instanceof ApiError)) throw error;
    if (error.status === 429) return { id, kind: "error", message: "Muitas sugestões em pouco tempo. Aguarde um instante ou preencha o formulário." };
    if (error.status === 0 || error.status >= 500) {
      return { id, kind: "error", message: "O assistente não está disponível agora. Preencha o formulário normalmente: o registro não é afetado." };
    }

    return { id, kind: "error", message: error.detail ? `${error.title}: ${error.detail}` : error.title };
  }
}

export async function registerEntryAction(_previous: FormState, form: FormData): Promise<FormState> {
  const type = form.get("type");
  const amount = parseAmount(String(form.get("amount") ?? ""));
  const entryDate = String(form.get("entryDate") ?? "");
  const description = String(form.get("description") ?? "").trim();
  const idempotencyKey = String(form.get("idempotencyKey") ?? "");

  if (type !== "Credit" && type !== "Debit") return fail("Escolha crédito ou débito.");
  if (amount === null) return fail("Informe um valor maior que zero, com até duas casas decimais (ex.: 1.234,56).");
  if (!isIsoDate(entryDate)) return fail("Informe uma data válida.");
  if (entryDate > todayInSaoPaulo()) return fail("A data do lançamento não pode ser no futuro.");
  if (description.length > 200) return fail("A descrição pode ter no máximo 200 caracteres.");
  if (!idempotencyKey) return fail("Formulário inválido. Recarregue a página.");

  try {
    const api = await apiForCurrentUser();
    await api.registerEntry({ type, amount, entryDate, description: description || undefined }, idempotencyKey);
  } catch (error) {
    return describe(error);
  }

  revalidatePath("/lancamentos");

  return { ok: true, message: "Lançamento registrado. O saldo será atualizado em instantes." };
}

export async function reverseEntryAction(form: FormData): Promise<void> {
  const id = String(form.get("id") ?? "");
  const idempotencyKey = String(form.get("idempotencyKey") ?? "");
  if (!id || !idempotencyKey) return;

  try {
    const api = await apiForCurrentUser();
    await api.reverseEntry(id, "Estorno pelo comerciante", idempotencyKey);
  } catch (error) {
    // The list shows the real state afterwards: an entry that is already reversed simply appears as reversed.
    if (!(error instanceof ApiError)) throw error;
  }

  revalidatePath("/lancamentos");
}
