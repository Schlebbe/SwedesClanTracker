import { BeveledButton } from "../components/osrs/BeveledButton";
import { EmptyFeatureState } from "../components/osrs/EmptyFeatureState";
import { StatusPill } from "../components/osrs/StatusPill";
import { StonePanel } from "../components/osrs/StonePanel";

export function ReadinessSurface({ readiness, loading, error, onRetry }) {
  const runtime = Array.isArray(readiness?.runtime) ? readiness.runtime : [];
  const config = Array.isArray(readiness?.config) ? readiness.config : [];

  return (
    <div className="page readiness-page">
      <header className="page-header"><div><h1>Readiness</h1><p>Runtime and configuration signals for the tracker.</p></div></header>
      {loading ? <StonePanel><StatusPill tone="info" loading>Loading readiness…</StatusPill></StonePanel> : null}
      {error ? <StonePanel tone="danger"><EmptyFeatureState title="Readiness could not be loaded" message={error} tone="danger" action={<BeveledButton variant="ghost" onClick={onRetry}>Retry</BeveledButton>} /></StonePanel> : null}
      {!loading && !error ? <div className="readiness-grid"><StonePanel title="Runtime" icon="health"><ul className="readiness-list">{runtime.length ? runtime.map((item) => <li key={item.label}><div><strong>{item.label}</strong><span>{item.detail}</span></div><StatusPill tone={item.tone}>{item.state}</StatusPill></li>) : <li><span>No runtime signals available.</span></li>}</ul></StonePanel><StonePanel title="Configuration" icon="settings"><ul className="readiness-list">{config.length ? config.map((item) => <li key={item.key}><strong>{item.key}</strong><span>{item.value}</span></li>) : <li><span>No configuration signals available.</span></li>}</ul></StonePanel></div> : null}
    </div>
  );
}
