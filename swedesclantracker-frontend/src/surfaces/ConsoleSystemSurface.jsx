import { IconGlyph } from "../components/osrs/IconGlyph";
import { ErrorState, Panel, PageFrame, State, StaleNotice, TimeStamp } from "../components/console/Primitives";
import { humanize, mapSystemToViewModel } from "../data/viewModels/consoleViewModels";
import { useConsoleRequest } from "../app/useConsoleRequest";
import { fetchConsoleSystem } from "../data/appDataApi";

export function ConsoleSystemSurface() {
  const request = useConsoleRequest("system", (signal) => fetchConsoleSystem({ signal }));
  const data = mapSystemToViewModel(request.data);
  if (request.loading && !request.data) return <PageFrame eyebrow="System" title="System" description="Loading runtime and configuration observations…"><div className="loading-grid"><div /><div /><div /></div></PageFrame>;
  if (request.error && !request.data) return <PageFrame eyebrow="System" title="System"><ErrorState error={request.error} onRetry={() => window.location.reload()} /></PageFrame>;

  return <PageFrame eyebrow="System" title="System" description="Factual runtime health and readiness. Secret values and private proxy data are never rendered.">
    {request.stale ? <StaleNotice /> : null}
    <Panel title="Runtime posture" eyebrow="Observed by the API and persisted Worker heartbeat" icon="health"><div className="system-check-grid">{data.checks.map((check) => <div className="system-check" key={check.component}><div className={`system-check-icon system-check-${check.state}`}><IconGlyph name={check.component === "PostgreSQL" ? "database" : check.component === "Worker" ? "activity" : check.component === "Readiness" ? "badge" : "shield"} size={18} /></div><div><strong>{check.component}</strong><State value={check.state} /><p>{check.detail}</p>{check.observedAt ? <TimeStamp value={check.observedAt} /> : null}</div></div>)}</div></Panel>
    <div className="system-grid"><Panel title="Configuration readiness" eyebrow={`${data.configuration.filter((item) => item.state === "configured").length} configured checks`} icon="settings"><div className="config-list">{data.configuration.map((item) => <div className="config-row" key={item.key}><div><strong>{item.key}</strong><small>{item.detail}</small></div><State value={item.state} label={humanize(item.state)} /></div>)}</div><p className="panel-footnote">Values are intentionally hidden. External reachability is not claimed by configuration presence; the Worker heartbeat remains the runtime source of truth.</p></Panel><Panel title="Health interpretation" eyebrow="State semantics" icon="info"><div className="state-legend"><Legend state="configured" detail="Required configuration value is present." /><Legend state="reachable" detail="A runtime check confirmed reachability." /><Legend state="stale" detail="The observed timestamp is older than the operational freshness window." /><Legend state="unavailable" detail="The check could not be completed or no value exists." /><Legend state="unknown" detail="The repository does not provide enough facts to classify the state." /></div><div className="warning-banner"><IconGlyph name="alert" size={17} /><span>Readiness key lookup uses the client configuration keys: TempleOsrs and WiseOldMan. Legacy Tracker-prefixed keys are not treated as authoritative.</span></div></Panel></div>
  </PageFrame>;
}

function Legend({ state, detail }) {
  return <div className="legend-row"><State value={state} /><span>{detail}</span></div>;
}
