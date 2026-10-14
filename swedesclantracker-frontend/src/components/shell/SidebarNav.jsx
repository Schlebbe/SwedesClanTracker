import { IconGlyph } from "../osrs/IconGlyph";

export function SidebarNav({ items, route, onNavigate, mobileOpen }) {
  return (
    <aside className={`osrs-sidebar ${mobileOpen ? "osrs-sidebar-open" : ""}`}>
      <div className="osrs-sidebar-brand">
        <div className="osrs-brand-mark" aria-hidden="true">
          <IconGlyph name="crest" className="osrs-brand-shield" />
        </div>
        <div>
          <strong>Swedes Clan</strong>
          <span>Tracker</span>
        </div>
      </div>

      <nav className="osrs-nav-list" aria-label="Primary">
        {items.map((item) => {
          const isActive = item.route === route.name || (item.route === "members" && route.name === "profile") || (item.route === "reviews" && route.name === "review-detail");
          return (
            <a
              key={item.id}
              href={item.href}
              className={isActive ? "osrs-nav-item osrs-nav-item-active" : "osrs-nav-item"}
              onClick={(event) => {
                if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
                event.preventDefault();
                onNavigate(item.href);
              }}
              aria-current={isActive ? "page" : undefined}
            >
              <IconGlyph name={item.icon} className="osrs-nav-icon" />
              <span>{item.label}</span>
              {item.badge ? <small>{item.badge}</small> : null}
            </a>
          );
        })}
      </nav>
      <div className="osrs-sidebar-note">
        <span>Read-only foundation</span>
        <small>Review execution remains gated.</small>
      </div>
    </aside>
  );
}
