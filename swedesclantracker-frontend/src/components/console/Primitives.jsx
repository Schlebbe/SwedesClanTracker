import { IconGlyph } from "../osrs/IconGlyph";
import { StatusPill } from "../osrs/StatusPill";
import { formatRelative, formatTimestamp, humanize, stateTone } from "../../data/viewModels/consoleViewModels";

export function PageFrame({ eyebrow, title, description, actions, children }) {
  return (
    <div className="page-frame">
      <div className="page-heading">
        <div>
          {eyebrow ? <span className="eyebrow">{eyebrow}</span> : null}
          <h1>{title}</h1>
          {description ? <p className="page-description">{description}</p> : null}
        </div>
        {actions ? <div className="page-actions">{actions}</div> : null}
      </div>
      {children}
    </div>
  );
}

export function Panel({ title, eyebrow, icon, actions, className = "", children }) {
  return (
    <section className={`console-panel ${className}`.trim()}>
      {(title || eyebrow || actions) ? (
        <div className="panel-heading">
          <div className="panel-heading-title">
            {icon ? <span className="panel-icon"><IconGlyph name={icon} size={17} /></span> : null}
            <div>
              {eyebrow ? <span className="eyebrow">{eyebrow}</span> : null}
              {title ? <h2>{title}</h2> : null}
            </div>
          </div>
          {actions ? <div className="panel-actions">{actions}</div> : null}
        </div>
      ) : null}
      <div className="panel-body">{children}</div>
    </section>
  );
}

export function Metric({ label, value, detail, tone = "neutral", icon }) {
  return (
    <div className="metric-block">
      <div className="metric-label">{icon ? <IconGlyph name={icon} size={14} /> : null}{label}</div>
      <strong className={`metric-value metric-${tone}`}>{value}</strong>
      {detail ? <span className="metric-detail">{detail}</span> : null}
    </div>
  );
}

export function State({ value, label, className = "" }) {
  return <StatusPill tone={stateTone(value)} className={className}>{label ?? humanize(value)}</StatusPill>;
}

export function TimeStamp({ value, className = "" }) {
  return value ? <time className={`timestamp ${className}`.trim()} dateTime={value} title={formatTimestamp(value)}>{formatTimestamp(value)} <span>· {formatRelative(value)}</span></time> : <span className="timestamp timestamp-unknown">No timestamp</span>;
}

export function ConsoleLink({ href, onNavigate, children, className = "", ...props }) {
  return (
    <a
      href={href}
      className={className}
      onClick={(event) => {
        if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || !onNavigate) return;
        event.preventDefault();
        onNavigate(href);
      }}
      {...props}
    >
      {children}
    </a>
  );
}

export function LoadingState({ label = "Loading persisted data…" }) {
  return <div className="state-box state-loading" role="status" aria-live="polite"><span className="loading-mark" aria-hidden="true" /><span>{label}</span></div>;
}

export function EmptyState({ title = "No records", detail = "The repository returned no matching records.", icon = "info" }) {
  return <div className="state-box state-empty"><IconGlyph name={icon} size={22} /><div><strong>{title}</strong><span>{detail}</span></div></div>;
}

export function ErrorState({ error, onRetry, title = "Data unavailable" }) {
  return (
    <div className="state-box state-error" role="alert">
      <IconGlyph name="alert" size={22} />
      <div><strong>{title}</strong><span>{error?.message ?? "The read-only projection could not be loaded."}</span></div>
      {onRetry ? <button type="button" className="text-button" onClick={onRetry}>Retry</button> : null}
    </div>
  );
}

export function StaleNotice({ children = "Showing cached data; the latest read failed." }) {
  return <div className="stale-notice" role="status"><IconGlyph name="clock" size={15} />{children}</div>;
}

export function ReadOnlyNotice() {
  return <div className="read-only-notice"><IconGlyph name="shield-alert" size={16} /><span>Read-only foundation. Review execution and external changes are not enabled.</span></div>;
}

export function EvidenceList({ evidence = [] }) {
  return (
    <dl className="evidence-list">
      {evidence.map((item, index) => (
        <div className="evidence-row" key={`${item.label}-${index}`}>
          <dt>{item.label}</dt>
          <dd className={item.state === "unknown" ? "value-unknown" : ""}>{item.value ?? "Unavailable"}</dd>
          {item.observedAt ? <dd className="evidence-time"><TimeStamp value={item.observedAt} /></dd> : null}
        </div>
      ))}
    </dl>
  );
}

export function DisabledActionList({ actions = [] }) {
  if (!actions.length) return null;
  return (
    <div className="descriptor-list" aria-label="Read-only action descriptors">
      {actions.map((action) => (
        <div className={`descriptor descriptor-${action.direction ?? "warning"}`} key={action.actionId}>
          <span>{action.label}</span>
          <small>{action.disabledReason ?? "Not available in this gate"}</small>
        </div>
      ))}
    </div>
  );
}
