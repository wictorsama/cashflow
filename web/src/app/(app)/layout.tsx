import Link from "next/link";
import { redirect } from "next/navigation";
import { auth, keycloakLogoutUrl, signOut } from "@/auth";
import { LogoMark, Wordmark } from "@/components/logo";
import { NavTabs, type NavItem } from "@/components/nav-tabs";
import { ThemeToggle } from "@/components/theme-toggle";
import { isAuditor } from "@/lib/roles";

export const dynamic = "force-dynamic";

const merchantNav: readonly NavItem[] = [
  { href: "/saldo", label: "Saldo" },
  { href: "/lancamentos", label: "Lançamentos" },
];
const auditorNav: readonly NavItem[] = [{ href: "/auditoria", label: "Auditoria" }];

export default async function AppLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  const session = await auth();
  if (!session?.accessToken || session.error) redirect("/entrar");

  const auditor = isAuditor(session);

  async function signOutAction() {
    "use server";
    const current = await auth();
    await signOut({ redirect: false });
    // Also end the Keycloak session, otherwise the next "Entrar" would sign in again without asking for the password.
    redirect(keycloakLogoutUrl(current?.idToken, process.env.AUTH_URL ?? "http://localhost:3000"));
  }

  return (
    <div className="shell">
      <header className="topbar">
        <Link className="brand" href={auditor ? "/auditoria" : "/saldo"} aria-label="CashFlow, página inicial">
          <LogoMark className="mark" />
          <span>
            <Wordmark />
          </span>
        </Link>
        <NavTabs items={auditor ? auditorNav : merchantNav} />
        <div className="who">
          {auditor ? (
            <span className="badge ro">Auditor · somente leitura</span>
          ) : (
            <span title={session.merchantId}>Comerciante {session.merchantId?.slice(0, 8) ?? "—"}</span>
          )}
          <ThemeToggle />
          <form action={signOutAction}>
            <button type="submit" className="secondary">Sair</button>
          </form>
        </div>
      </header>
      <main>{children}</main>
    </div>
  );
}
