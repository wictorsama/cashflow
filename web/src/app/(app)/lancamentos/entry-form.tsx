"use client";

import { useActionState, useEffect, useState } from "react";
import { draftEntryAction, registerEntryAction, type DraftState, type FormState } from "./actions";

const initial: FormState = { ok: false, message: "" };
const noDraft: DraftState = { id: 0, kind: "none", message: "" };

interface Fields {
  type: string;
  amount: string;
  entryDate: string;
  description: string;
}

export function EntryForm({ today }: Readonly<{ today: string }>) {
  const [state, action, pending] = useActionState(registerEntryAction, initial);
  const [draft, draftAction, drafting] = useActionState(draftEntryAction, noDraft);
  const blank: Fields = { type: "Credit", amount: "", entryDate: today, description: "" };
  const [fields, setFields] = useState<Fields>(blank);
  // One key per form submission attempt: a double click or a retry sends the same key, so the entry is saved once.
  // After a success the key is renewed, so the next entry is a new one.
  const [key, setKey] = useState(() => crypto.randomUUID());

  useEffect(() => {
    if (state.ok) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- clearing the form is the reaction to the success
      setFields({ type: "Credit", amount: "", entryDate: today, description: "" });
      setKey(crypto.randomUUID());
    }
  }, [state, today]);

  // The assistant only fills the fields. The person reviews them and presses Registrar: that is the only way to record.
  useEffect(() => {
    if (draft.kind === "draft" && draft.draft) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- filling the form is the reaction to the answer
      setFields({ ...draft.draft });
      setKey(crypto.randomUUID()); // a suggestion is a new entry, never a retry of the previous one
    }
  }, [draft]);

  const set = (name: keyof Fields) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) =>
    setFields((current) => ({ ...current, [name]: e.target.value }));

  const draftClass = draft.kind === "draft" ? "ok" : draft.kind === "question" ? "" : "err";

  return (
    <div className="card">
      <h2>Novo lançamento</h2>

      <form action={draftAction} className="inline" style={{ marginBottom: 10 }}>
        <label className="flex1">
          Assistente · descreva o lançamento (opcional)
          <input
            name="text"
            maxLength={500}
            autoComplete="off"
            placeholder="Ex.: paguei 150 de luz ontem"
            aria-describedby="assistant-help"
          />
        </label>
        <button type="submit" disabled={drafting}>
          {drafting ? "Pensando…" : "Sugerir preenchimento"}
        </button>
      </form>
      <p id="assistant-help" className="muted small" style={{ margin: "0 0 14px" }}>
        O assistente só sugere: você confere e confirma. Se ele não estiver disponível, o formulário abaixo funciona igual.
      </p>
      {draft.message && (
        <div className={`notice ${draftClass}`} role={draft.kind === "draft" ? "status" : "alert"}>
          {draft.message}
        </div>
      )}

      {state.message && (
        <div className={`notice ${state.ok ? "ok" : "err"}`} role={state.ok ? "status" : "alert"}>
          {state.message}
        </div>
      )}
      <form action={action} className="inline">
        <input type="hidden" name="idempotencyKey" value={key} />
        {/* A new suggestion remounts this group, which replays the "filled in" animation on the fields. */}
        <div key={draft.kind === "draft" ? draft.id : 0} className={draft.kind === "draft" ? "flash" : undefined} style={{ display: "contents" }}>
          <label>
            Tipo
            <select name="type" value={fields.type} onChange={set("type")}>
              <option value="Credit">Crédito (entrada)</option>
              <option value="Debit">Débito (saída)</option>
            </select>
          </label>
          <label>
            Valor (R$)
            <input name="amount" inputMode="decimal" placeholder="0,00" required autoComplete="off" value={fields.amount} onChange={set("amount")} />
          </label>
          <label>
            Data
            <input type="date" name="entryDate" max={today} required value={fields.entryDate} onChange={set("entryDate")} />
          </label>
          <label className="flex1">
            Descrição (opcional)
            <input name="description" maxLength={200} autoComplete="off" value={fields.description} onChange={set("description")} />
          </label>
        </div>
        <button type="submit" disabled={pending}>
          {pending ? "Salvando…" : "Registrar"}
        </button>
      </form>
    </div>
  );
}
