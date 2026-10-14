import { useState } from "react";
import { SidebarNav } from "./SidebarNav";
import { TopStatusBar } from "./TopStatusBar";
import { IconGlyph } from "../osrs/IconGlyph";

export function AppShell({ navItems, route, session, health, onNavigate, onLogout, children }) {
  const [mobileOpen, setMobileOpen] = useState(false);

  function handleNavigate(path) {
    setMobileOpen(false);
    onNavigate(path);
  }

  return (
    <main className="osrs-app-shell">
      <button
        className="mobile-nav-toggle"
        type="button"
        aria-label={mobileOpen ? "Close navigation" : "Open navigation"}
        aria-expanded={mobileOpen}
        onClick={() => setMobileOpen((open) => !open)}
      >
        <IconGlyph name={mobileOpen ? "close" : "menu"} />
      </button>
      <SidebarNav items={navItems} route={route} onNavigate={handleNavigate} mobileOpen={mobileOpen} />
      <section className="osrs-main">
        <TopStatusBar session={session} health={health} onLogout={onLogout} />
        <div className="osrs-content">
          {children}
        </div>
      </section>
    </main>
  );
}
