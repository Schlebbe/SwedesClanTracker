import { IconGlyph } from "./IconGlyph";

export function StonePanel({
  title,
  subtitle,
  actions,
  tone = "neutral",
  icon,
  variant = "default",
  compact = false,
  footer,
  children,
  className = "",
}) {
  const classes = [
    "osrs-stone-panel",
    variant !== "default" ? `osrs-stone-panel-${variant}` : "",
    tone !== "neutral" ? `osrs-stone-panel-${tone}` : "",
    compact ? "osrs-stone-panel-compact" : "",
    className,
  ].filter(Boolean).join(" ");

  return (
    <section className={classes}>
      {(title || subtitle || actions) ? (
        <header className="osrs-panel-header">
          <div className="osrs-panel-title">
            {icon ? <IconGlyph name={icon} className="osrs-panel-icon" /> : null}
            <div>
              {title ? <h3>{title}</h3> : null}
              {subtitle ? <p>{subtitle}</p> : null}
            </div>
          </div>
          {actions ? <div className="osrs-panel-actions">{actions}</div> : null}
        </header>
      ) : null}
      {children}
      {footer ? <footer className="osrs-panel-footer">{footer}</footer> : null}
    </section>
  );
}
