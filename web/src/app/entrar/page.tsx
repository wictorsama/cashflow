import { redirect } from "next/navigation";
import { auth, signIn } from "@/auth";
import { LogoMark, Wordmark } from "@/components/logo";
import { ThemeToggle } from "@/components/theme-toggle";

export const dynamic = "force-dynamic";

export default async function SignInPage() {
  const session = await auth();
  if (session?.accessToken && !session.error) redirect("/saldo");

  return (
    <div className="center">
      <div className="card login">
        <div style={{ position: "absolute", right: 14, top: 14 }}>
          <ThemeToggle />
        </div>
        <div className="glow" />
        <LogoMark className="mark" />
        <h1 className="wordmark">
          <Wordmark />
        </h1>
        <p className="sub">Controle de lançamentos e saldo diário consolidado, em tempo quase real.</p>
        <svg className="ecg" viewBox="0 0 300 54" preserveAspectRatio="none" aria-hidden="true">
          <path d="M0 32 H70 l8-3 8 3 H110 l8-22 10 44 10-34 8 12 H300" fill="none" stroke="var(--acc)" strokeWidth="2" />
        </svg>
        {session?.error && <p className="notice warn">Sua sessão expirou. Entre novamente.</p>}
        <form
          action={async () => {
            "use server";
            await signIn("keycloak", { redirectTo: "/saldo" });
          }}
        >
          <button type="submit">Entrar</button>
        </form>
        <p className="muted small" style={{ marginTop: 16, marginBottom: 0 }}>
          A autenticação é feita pelo Keycloak; esta aplicação nunca vê a sua senha.
        </p>
      </div>
    </div>
  );
}
