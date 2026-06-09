import { BeveledButton } from "../components/osrs/BeveledButton";
import { DataTable } from "../components/osrs/DataTable";
import { EmptyFeatureState } from "../components/osrs/EmptyFeatureState";
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
      <header className="surface-header dashboard-header">
        <div>
          <p className="eyebrow">Dashboard</p>
          <h2>{dashboard.title}</h2>
          <p>{dashboard.subtitle}</p>
        </div>
        <BeveledButton variant="secondary" onClick={onOpenQueue}>Open Admin Queue</BeveledButton>
      </header>

      <section className="dashboard-stat-grid" aria-label="Dashboard metrics">
        {dashboard.statCards.map((card) => (
          <StatCard
            key={card.key}
            label={card.label}
            value={card.value}
            detail={card.detail}
            tone={card.tone}
            available={card.available}
          />
        ))}
        {dashboard.futureStats.map((card) => (
          <StatCard
            key={card.key}
            label={card.label}
            icon={card.icon}
            available={card.available}
            unavailableReason={card.unavailableReason}
          />
        ))}
      </section>

      <section className="dashboard-main-grid">
        <StonePanel title="Tracker Health" subtitle="Live worker status is merged with the dashboard health snapshot.">
          <div className="dashboard-health-grid">
            {dashboard.healthCards.map((item) => (
              <StatusBlock key={item.key} item={item} />
            ))}
          </div>
          {dashboard.liveStatus.loading ? <StatusPill tone="info" loading>Connecting to live status...</StatusPill> : null}
          {dashboard.liveStatus.error ? (
            <div className="live-status-note">
              <StatusPill tone={dashboard.liveStatus.stale ? "warning" : "danger"}>
                {dashboard.liveStatus.stale ? "Showing last known worker status." : "Live worker status unavailable."}
              </StatusPill>
              <BeveledButton variant="ghost" onClick={onRetryLive}>Retry live status</BeveledButton>
            </div>
          ) : null}
        </StonePanel>

        <StonePanel
          title="Pending Admin Work"
          subtitle="Current review and promotion work from the existing admin queue preview."
          actions={<BeveledButton variant="primary" onClick={onOpenQueue}>Review</BeveledButton>}
        >
          {dashboard.workItems.length ? (
            <ul className="dashboard-card-list">
              {dashboard.workItems.map((item) => (
                <li key={item.key} className="dashboard-work-item">
                  <div>
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
      </section>

      <section className="dashboard-main-grid">
        <StonePanel title="Roster Posture" subtitle="Counts supported by the current home endpoint.">
          {dashboard.postureCards.length ? (
            <div className="dashboard-posture-grid">
              {dashboard.postureCards.map((item) => (
                <StatusBlock key={item.key} item={item} />
              ))}
            </div>
          ) : (
            <EmptyFeatureState title="No roster posture returned" message="The API did not return stale, missing, merge, or rank-mismatch counts." />
          )}
        </StonePanel>

        <StonePanel title="Future Tracking" subtitle="Shown as unavailable because the current backend does not collect these metrics.">
          <div className="dashboard-unavailable-grid">
            <UnavailableMetric label="Drops and splits" reason="Requires a drops/splits source and persisted domain model." />
            <UnavailableMetric label="Competitions" reason="Requires competition rules, participants, and scoring windows." />
          </div>
        </StonePanel>
      </section>

      <StonePanel title="Recent Meaningful Clan Changes" subtitle="Current lifecycle highlights from the existing dashboard response.">
        <DataTable
          columns={[
            { key: "event", header: "Event" },
            { key: "category", header: "Category", render: (row) => <StatusPill tone={row.tone}>{row.category}</StatusPill> },
            { key: "time", header: "Time" },
          ]}
          rows={dashboard.recentChanges}
          emptyTitle="No recent changes"
          emptyMessage="No meaningful clan changes were recorded in the current window."
        />
      </StonePanel>
    </div>
  );
}

function StatusBlock({ item }) {
  return (
    <div className="dashboard-status-block">
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
