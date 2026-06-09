import { IconGlyph } from "../osrs/IconGlyph";

const defaultBottomItems = [
  { id: "settings", label: "Settings", icon: "settings", disabled: true },
  { id: "support", label: "Support", icon: "support", disabled: true },
];

export function SidebarNav({ items, activeItem, onSelect, bottomItems = defaultBottomItems }) {
  return (
    <aside className="osrs-sidebar">
      <div className="osrs-sidebar-brand">
        <div className="osrs-brand-mark" aria-hidden="true">
          <span className="osrs-brand-shield">
            <span />
          </span>
        </div>
        <div>
          <strong>Clan Hub Admin</strong>
          <span>Swedes Clan</span>
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
              disabled={item.disabled}
              aria-current={isActive ? "page" : undefined}
            >
              <IconGlyph name={item.icon} className="osrs-nav-icon" />
              <span>{item.label}</span>
              {item.badge ? <small>{item.badge}</small> : null}
            </button>
          );
        })}
      </nav>

      <div className="osrs-sidebar-bottom">
        {bottomItems.map((item) => (
          <button
            key={item.id}
            type="button"
            className="osrs-nav-item osrs-nav-item-muted"
            disabled={item.disabled !== false}
            onClick={item.onSelect}
          >
            <IconGlyph name={item.icon ?? "default"} className="osrs-nav-icon" />
            <span>{item.label}</span>
          </button>
        ))}
      </div>
    </aside>
  );
}
