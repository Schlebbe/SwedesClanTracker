import { BeveledButton } from "../components/osrs/BeveledButton";
import { DataTable } from "../components/osrs/DataTable";
import { EmptyFeatureState } from "../components/osrs/EmptyFeatureState";
import { IconGlyph } from "../components/osrs/IconGlyph";
import { StatCard } from "../components/osrs/StatCard";
import { StatusPill } from "../components/osrs/StatusPill";
import { StonePanel } from "../components/osrs/StonePanel";
import { UnavailableMetric } from "../components/osrs/UnavailableMetric";
import { mapHomeToDashboardViewModel } from "../data/viewModels/dashboardViewModel";

export function DashboardSurface({ data, liveStatus, loading, error, onRetry, onRetryLive, onOpenQueue }) {
  if (loading) {
    return <SurfaceMessage title="Dashboard" text="Loading dashboard overview..." tone="info" loading />;
  }

  if (error) {
    return (
      <SurfaceMessage
        title="Dashboard"
        text={`Unable to load dashboard: ${error}`}
        tone="danger"
        action={<BeveledButton variant="ghost" onClick={onRetry}>Retry</BeveledButton>}
      />
    );
  }

  if (!data) {
    return (
      <SurfaceMessage
        title="Dashboard"
        text="No dashboard data was returned by the API."
        tone="warning"
        action={<BeveledButton variant="ghost" onClick={onRetry}>Retry</BeveledButton>}
      />
    );
  }

  const dashboard = mapHomeToDashboardViewModel(data, liveStatus);

  return (
    <div className="surface-grid dashboard-surface">
      <header className="dashboard-command-header">
        <div>
          <h2>{dashboard.title}</h2>
          <p>{dashboard.subtitle}</p>
        </div>
        <div className="dashboard-command-actions">
          <BeveledButton variant="primary" icon="review" onClick={onOpenQueue}>Review Queue</BeveledButton>
        </div>
      </header>

      <section className="dashboard-kpi-row" aria-label="Dashboard metrics">
        {dashboard.statCards.map((card) => (
          <StatCard
            key={card.key}
            label={card.label}
            value={card.value}
            detail={card.detail}
            icon={dashboardIconForKey(card.key)}
            tone={card.tone}
            available={card.available}
            variant="hero"
          />
        ))}
      </section>

      <section className="dashboard-live-grid">
        <div className="dashboard-primary-stack">
          <StonePanel
            title="Pending Admin Work"
            icon="review"
            variant="featured"
            actions={<BeveledButton variant="secondary" icon="review" onClick={onOpenQueue}>Open Queue</BeveledButton>}
          >
            {dashboard.workItems.length ? (
              <ul className="dashboard-work-grid">
                {dashboard.workItems.map((item) => (
                  <li key={item.key} className="dashboard-work-card">
                    <IconGlyph name={workIconForTone(item.tone)} className="dashboard-work-icon" />
                    <div className="dashboard-work-copy">
                      <strong>{item.label}</strong>
                      <p>{item.detail || "No case detail available"}</p>
                    </div>
                    <StatusPill tone={item.tone}>{item.risk}</StatusPill>
                  </li>
                ))}
              </ul>
            ) : (
              <EmptyFeatureState title="No admin work waiting" message="The current dashboard preview returned no open work items." />
            )}
          </StonePanel>

          <StonePanel title="Recent Clan Activity" icon="activity" variant="table">
            <DataTable
              className="dashboard-activity-table"
              columns={[
                { key: "event", header: "Event" },
                { key: "category", header: "Category", render: (row) => <StatusPill tone={row.tone}>{row.category}</StatusPill> },
                { key: "time", header: "Time" },
              ]}
              rows={dashboard.recentChanges}
              emptyTitle="No recent changes"
              emptyMessage="No meaningful clan changes were recorded in the current window."
              footer={<span>Current dashboard feed from the existing home endpoint.</span>}
            />
          </StonePanel>
        </div>

        <aside className="dashboard-side-stack" aria-label="Dashboard support sections">
          <StonePanel title="Tracker Health" icon="readiness" variant="featured">
            <div className="dashboard-health-panel">
              {dashboard.healthCards[0] ? (
                <StatusBlock
                  item={dashboard.healthCards[0]}
                  icon={dashboardIconForKey(dashboard.healthCards[0].key)}
                  className="dashboard-status-block-primary"
                />
              ) : null}
              <div className="dashboard-health-list">
                {dashboard.healthCards.slice(1).map((item) => (
                  <StatusBlock key={item.key} item={item} compact />
                ))}
              </div>
            </div>
            {dashboard.liveStatus.loading ? <StatusPill tone="info" loading>Connecting to live status...</StatusPill> : null}
            {dashboard.liveStatus.error ? (
              <div className="live-status-note">
                <StatusPill tone={dashboard.liveStatus.stale ? "warning" : "danger"}>
                  {dashboard.liveStatus.stale ? "Showing last known worker status." : "Live worker status unavailable."}
                </StatusPill>
                <BeveledButton variant="ghost" icon="refresh" onClick={onRetryLive}>Retry live status</BeveledButton>
              </div>
            ) : null}
          </StonePanel>

          <StonePanel title="Roster Posture" icon="members">
            {dashboard.postureCards.length ? (
              <div className="dashboard-posture-grid">
                {dashboard.postureCards.map((item) => (
                  <StatusBlock key={item.key} item={item} compact />
                ))}
              </div>
            ) : (
              <EmptyFeatureState title="No roster posture returned" message="The API did not return stale, missing, merge, or rank-mismatch counts." />
            )}
          </StonePanel>

          <StonePanel title="Future Telemetry" icon="future" variant="muted" compact>
            <div className="dashboard-future-strip">
              {dashboard.futureStats.map((card) => (
                <UnavailableMetric key={card.key} label={card.label} reason={card.unavailableReason} />
              ))}
              <UnavailableMetric label="Drops and splits" reason="Requires a drops/splits source and persisted domain model." />
              <UnavailableMetric label="Competitions" reason="Requires competition rules, participants, and scoring windows." />
            </div>
          </StonePanel>
        </aside>
      </section>
    </div>
  );
}

function StatusBlock({ item, icon, compact = false, className = "" }) {
  const classes = [
    "dashboard-status-block",
    compact ? "dashboard-status-block-compact" : "",
    className,
  ].filter(Boolean).join(" ");

  return (
    <div className={classes}>
      {icon ? <IconGlyph name={icon} className="dashboard-status-icon" /> : null}
      <div>
        <span>{item.label}</span>
        <strong>{item.value}</strong>
        {item.detail ? <p>{item.detail}</p> : null}
      </div>
      <StatusPill tone={item.tone}>{item.statusLabel ?? toneLabel(item.tone)}</StatusPill>
    </div>
  );
}

function toneLabel(tone) {
  if (tone === "success") return "Clear";
  if (tone === "warning") return "Watch";
  if (tone === "danger") return "Issue";
  if (tone === "info") return "Info";
  return "OK";
}

function dashboardIconForKey(key) {
  const icons = {
    "tracked-members": "members",
    "pending-promotions": "promotion",
    "open-admin-cases": "review",
    overall: "health",
    api: "status",
    worker: "refresh",
    "latest-sync": "user-refresh",
    "latest-event": "recent",
  };

  return icons[key] ?? "default";
}

function workIconForTone(tone) {
  if (tone === "danger") return "danger";
  if (tone === "warning") return "member-alert";
  if (tone === "success") return "success";
  return "review";
}

function SurfaceMessage({ title, text, tone, action = null, loading = false }) {
  return (
    <div className="surface-grid">
      <header className="surface-header">
        <p className="eyebrow">{title}</p>
      </header>
      <section className="panel">
        <StatusPill tone={tone ?? "neutral"} loading={loading}>{text}</StatusPill>
        {action ? <div className="message-action">{action}</div> : null}
      </section>
    </div>
  );
}
