import { BeveledButton } from "../components/osrs/BeveledButton";
import { EmptyFeatureState } from "../components/osrs/EmptyFeatureState";
import { StatusPill } from "../components/osrs/StatusPill";
import { StonePanel } from "../components/osrs/StonePanel";
import { mapHomeToDashboardViewModel } from "../data/viewModels/dashboardViewModel";

export function DashboardSurface({ data, liveStatus, loading, error, onRetry, onOpenQueue, onOpenMembers }) {
  if (loading) return <SurfaceState title="Dashboard" message="Loading tracker data…" loading />;
  if (error) return <SurfaceState title="Dashboard" message={`Unable to load tracker data: ${error}`} tone="danger" action={<BeveledButton variant="ghost" onClick={onRetry}>Retry</BeveledButton>} />;
  if (!data) return <SurfaceState title="Dashboard" message="No tracker data is available." tone="warning" action={<BeveledButton variant="ghost" onClick={onRetry}>Retry</BeveledButton>} />;

  const dashboard = mapHomeToDashboardViewModel(data, liveStatus);
  const attention = dashboard.posture.filter((item) => item.value !== null && item.value > 0);

  return (
    <div className="page dashboard-page">
      <header className="page-header">
        <div>
          <h1>{dashboard.title}</h1>
          <p>{dashboard.subtitle}</p>
        </div>
        <div className="page-header-actions">
          <BeveledButton variant="secondary" icon="members" onClick={onOpenMembers}>View members</BeveledButton>
          <BeveledButton variant="primary" icon="review" onClick={onOpenQueue}>Review work</BeveledButton>
        </div>
      </header>

      <section className="summary-strip" aria-label="Tracker summary">
        {dashboard.summary.map((item) => (
          <div className="summary-item" key={item.key}>
            <span>{item.label}</span>
            <strong>{item.value === null ? "Unavailable" : item.value.toLocaleString()}</strong>
            <small>{item.detail}</small>
          </div>
        ))}
      </section>

      <div className="dashboard-grid">
        <StonePanel title="Tracker health" icon="health" className="dashboard-health-panel">
          <div className="health-list">
            {dashboard.healthRows.map((row) => (
              <div className="health-row" key={row.label}>
                <span>{row.label}</span>
                <div>
                  <strong>{row.value}</strong>
                  <small>{row.detail}</small>
                </div>
                <StatusPill tone={row.tone}>{row.value}</StatusPill>
              </div>
            ))}
          </div>
        </StonePanel>

        <StonePanel title="Roster attention" icon="member-alert" className="dashboard-attention-panel">
          {attention.length ? (
            <ul className="attention-list">
              {attention.map((item) => (
                <li key={item.label}>
                  <div>
                    <strong>{item.label}</strong>
                    <span>{item.hint}</span>
                  </div>
                  <StatusPill tone={item.tone}>{item.value.toLocaleString()}</StatusPill>
                </li>
              ))}
            </ul>
          ) : (
            <EmptyFeatureState title="Roster is clear" message="No stale, missing, merge, or rank-mismatch signals are open." />
          )}
        </StonePanel>

        <StonePanel title="Open work" icon="review" className="dashboard-work-panel">
          {dashboard.workItems.length ? (
            <ul className="work-list">
              {dashboard.workItems.map((item) => (
                <li key={item.id}>
                  <div>
                    <strong>{item.label}</strong>
                    <span>{item.age}</span>
                  </div>
                  <StatusPill tone={item.tone}>{item.risk}</StatusPill>
                </li>
              ))}
            </ul>
          ) : (
            <EmptyFeatureState title="No open work" message="The current API has no review cases waiting for action." />
          )}
          <div className="panel-action-row">
            <BeveledButton variant="ghost" onClick={onOpenQueue}>Open review queues</BeveledButton>
          </div>
        </StonePanel>

        <StonePanel title="Recent changes" icon="activity" className="dashboard-activity-panel">
          {dashboard.activity.length ? (
            <ul className="change-list">
              {dashboard.activity.map((item) => (
                <li key={item.id}>
                  <time>{item.time}</time>
                  <div>
                    <strong>{item.title}</strong>
                    <span>{item.category}</span>
                  </div>
                  <StatusPill tone={item.tone}>{item.category}</StatusPill>
                </li>
              ))}
            </ul>
          ) : (
            <EmptyFeatureState title="No recent changes" message="The activity feed has no recent events." />
          )}
        </StonePanel>
      </div>
    </div>
  );
}

function SurfaceState({ title, message, tone = "info", loading = false, action = null }) {
  return (
    <div className="page surface-state">
      <header className="page-header"><h1>{title}</h1></header>
      <StonePanel tone={tone}>
        <StatusPill tone={tone} loading={loading}>{message}</StatusPill>
        {action ? <div className="panel-action-row">{action}</div> : null}
      </StonePanel>
    </div>
  );
}
