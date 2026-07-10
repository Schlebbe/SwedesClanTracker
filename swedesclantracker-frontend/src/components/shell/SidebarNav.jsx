import { IconGlyph } from "../osrs/IconGlyph";

export function SidebarNav({ items, activeItem, onSelect }) {
  return (
    <aside className="osrs-sidebar">
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
          const isActive = activeItem === item.id;
          return (
            <button
              key={item.id}
              type="button"
              className={isActive ? "osrs-nav-item osrs-nav-item-active" : "osrs-nav-item"}
              onClick={() => onSelect(item.id)}
              aria-current={isActive ? "page" : undefined}
            >
              <IconGlyph name={item.icon} className="osrs-nav-icon" />
              <span>{item.label}</span>
              {item.badge ? <small>{item.badge}</small> : null}
            </button>
          );
        })}
      </nav>
    </aside>
  );
}
