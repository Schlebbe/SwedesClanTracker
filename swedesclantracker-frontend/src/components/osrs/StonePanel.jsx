export function StonePanel({ title, subtitle, actions, tone = "neutral", children, className = "" }) {
  const classes = ["osrs-stone-panel", tone !== "neutral" ? `osrs-stone-panel-${tone}` : "", className].filter(Boolean).join(" ");

  return (
    <section className={classes}>
      {(title || subtitle || actions) ? (
        <header className="osrs-panel-header">
          <div>
            {title ? <h3>{title}</h3> : null}
            {subtitle ? <p>{subtitle}</p> : null}
          </div>
          {actions ? <div className="osrs-panel-actions">{actions}</div> : null}
        </header>
      ) : null}
      {children}
    </section>
  );
}
