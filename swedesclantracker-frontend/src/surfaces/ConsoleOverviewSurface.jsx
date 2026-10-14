import { IconGlyph } from "../components/osrs/IconGlyph";
import { ConsoleLink, EmptyState, Metric, Panel, PageFrame, State, StaleNotice, TimeStamp } from "../components/console/Primitives";
import { formatMetric, humanize } from "../data/viewModels/consoleViewModels";
import { mapOverviewToViewModel } from "../data/viewModels/consoleViewModels";
import { useConsoleRequest } from "../app/useConsoleRequest";
import { fetchConsoleOverview } from "../data/appDataApi";

export function ConsoleOverviewSurface({ data, loading, error, stale, onRetry, onNavigate }) {
  const request = useConsoleRequest("overview", (signal) => fetchConsoleOverview({ signal }), !data);
  const requestData = mapOverviewToViewModel(request.data);
  const hasData = Boolean(data || request.data);
  const isLoading = loading ?? request.loading;
  const loadError = error ?? request.error;
  const isStale = stale || request.stale;
  if (isLoading && !hasData) return <PageFrame eyebrow="Daily watch" title="Overview" description="Loading the current tracker brief…"><div className="loading-grid"><div /><div /><div /></div></PageFrame>;
  if (loadError && !hasData) return <PageFrame eyebrow="Daily watch" title="Overview"><div className="surface-error"><strong>Overview unavailable</strong><span>{loadError.message}</span><button type="button" className="text-button" onClick={onRetry ?? (() => window.location.reload())}>Retry</button></div></PageFrame>;

  const view = data ?? requestData;
  const health = view.health;
  const totalWork = view.lanes.reduce((sum, lane) => sum + (lane.count ?? 0), 0);
  const totalAttention = view.attention.reduce((sum, item) => sum + (item.count ?? 0), 0);

  return (
    <PageFrame
      eyebrow="Daily watch"
      title="Overview"
      description="A factual operating brief for tracker health, open work, and roster attention."
      actions={<ConsoleLink href="/system" onNavigate={onNavigate} className="outline-link"><IconGlyph name="settings" size={15} />System</ConsoleLink>}
    >
      {isStale ? <StaleNotice /> : null}
      <section className="health-brief" aria-label="Tracker health brief">
        <div className="health-brief-main">
          <div className="eyebrow">Tracker posture</div>
          <div className="health-headline"><State value={health?.overall ?? "unknown"} /> <strong>{humanize(health?.overall ?? "unknown")}</strong></div>
          <p>{health?.readiness?.detail ?? "Readiness state is unavailable."}</p>
          <div className="health-timestamps">
            <span><b>Latest sync</b> {health?.latestSync?.detail ?? "Unavailable"}</span>
            <span><b>Worker</b> {health?.worker?.detail ?? "Unavailable"}</span>
          </div>
        </div>
        <div className="health-check-list">
          <HealthCheck label="API" component={health?.api} />
          <HealthCheck label="Database" component={health?.database} />
          <HealthCheck label="Readiness" component={health?.readiness} />
        </div>
      </section>

      <section className="metric-strip" aria-label="Overview counts">
        <Metric label="Open work" value={formatMetric(totalWork)} detail="Typed read-only lanes" tone={totalWork ? "warning" : "success"} icon="review" />
        <Metric label="Roster attention" value={formatMetric(totalAttention)} detail="Freshness or review facts" tone={totalAttention ? "warning" : "success"} icon="alert" />
        <Metric label="Readiness" value={`${health?.readiness?.configuredCount ?? 0}/${health?.readiness?.requiredCount ?? 0}`} detail="Required keys present" tone={health?.readiness?.state === "configured" ? "success" : "warning"} icon="badge" />
        <Metric label="Meaningful changes" value={formatMetric(view.recentChanges.length)} detail="Recent lifecycle facts" tone="info" icon="activity" />
      </section>

      <div className="overview-grid">
        <Panel title="Open work" eyebrow="Decision lanes" icon="review" actions={<ConsoleLink href="/reviews" onNavigate={onNavigate} className="panel-link">Open reviews <IconGlyph name="arrow-right" size={14} /></ConsoleLink>}>
          {view.lanes.length ? <div className="lane-list">{view.lanes.map((lane) => <ConsoleLink className="lane-row" key={lane.lane} href={lane.route} onNavigate={onNavigate}><span className={`lane-marker lane-marker-${lane.tone}`} /><span className="lane-name"><strong>{lane.label}</strong><small>{lane.oldestAt ? `Oldest ${lane.oldestAt}` : "No oldest timestamp"}</small></span><b>{lane.count}</b><IconGlyph name="chevron-right" size={16} /></ConsoleLink>)}</div> : <EmptyState title="No open work" detail="No persisted review or processing cases are currently projected." icon="check" />}
          <p className="panel-footnote">Counts come from persisted promotion candidates and open lifecycle signals. They are not executable here.</p>
        </Panel>

        <Panel title="Roster attention" eyebrow="Factual indicators" icon="members" actions={<ConsoleLink href="/members" onNavigate={onNavigate} className="panel-link">Open members <IconGlyph name="arrow-right" size={14} /></ConsoleLink>}>
          {view.attention.length ? <div className="attention-list">{view.attention.map((item) => <ConsoleLink className="attention-row" key={item.kind} href={item.route} onNavigate={onNavigate}><span className={`attention-dot attention-dot-${item.tone}`} /><span><strong>{item.label}</strong><small>{item.oldestAt ? `Oldest ${item.oldestAt}` : "No open observation"}</small></span><b>{item.count}</b><IconGlyph name="chevron-right" size={16} /></ConsoleLink>)}</div> : <EmptyState title="No attention signals" detail="Roster freshness and review indicators are clear." icon="check" />}
          <p className="panel-footnote">No confidence, percentage, or unsupported account metrics are used.</p>
        </Panel>

        <Panel title="Recent changes" eyebrow="Meaningful lifecycle facts" icon="activity" actions={<ConsoleLink href="/activity" onNavigate={onNavigate} className="panel-link">View activity <IconGlyph name="arrow-right" size={14} /></ConsoleLink>} className="overview-changes">
          {view.recentChanges.length ? <div className="change-list">{view.recentChanges.map((change) => <div className="change-row" key={change.eventId}><div className="change-icon"><IconGlyph name={change.eventType?.includes("PROMOTION") ? "badge" : change.eventType?.includes("MISSING") ? "alert" : "activity"} size={15} /></div><div className="change-copy"><strong>{change.label}</strong><span>{change.detail}</span><small>{change.playerName ?? "Anchor-only event"}{change.source ? ` · ${change.source}` : ""}</small></div><TimeStamp value={change.occurredAt} /></div>)}</div> : <EmptyState title="No recent changes" detail="No non-status lifecycle events are available." icon="history" />}
        </Panel>
      </div>
    </PageFrame>
  );
}

function HealthCheck({ label, component }) {
  return <div className="health-check"><span className={`health-check-dot health-check-${component?.state ?? "unknown"}`} /><span><strong>{label}</strong><small>{component?.state ? humanize(component.state) : "Unknown"}</small></span></div>;
}
