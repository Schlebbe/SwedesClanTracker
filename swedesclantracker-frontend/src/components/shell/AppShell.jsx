import { SidebarNav } from "./SidebarNav";
import { TopStatusBar } from "./TopStatusBar";

export function AppShell({ navItems, activeItem, onSelectItem, home, liveStatus, children }) {
  return (
    <main className="osrs-app-shell">
      <SidebarNav items={navItems} activeItem={activeItem} onSelect={onSelectItem} />
      <section className="osrs-main">
        <TopStatusBar home={home} liveStatus={liveStatus} />
        <div className="osrs-content">
          {children}
        </div>
      </section>
    </main>
  );
}
