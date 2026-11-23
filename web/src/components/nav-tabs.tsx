"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";

export interface NavItem {
  href: string;
  label: string;
}

export function NavTabs({ items }: Readonly<{ items: readonly NavItem[] }>) {
  const pathname = usePathname();

  return (
    <nav className="tabs" aria-label="Principal">
      {items.map((item) => (
        <Link key={item.href} href={item.href} aria-current={pathname.startsWith(item.href) ? "page" : undefined}>
          {item.label}
        </Link>
      ))}
    </nav>
  );
}
