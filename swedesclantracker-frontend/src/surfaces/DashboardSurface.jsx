import { BeveledButton } from "../components/osrs/BeveledButton";
import { DataTable } from "../components/osrs/DataTable";
import { IconGlyph } from "../components/osrs/IconGlyph";
import { StatCard } from "../components/osrs/StatCard";
import { StatusPill } from "../components/osrs/StatusPill";
import { StonePanel } from "../components/osrs/StonePanel";
import { mapHomeToDashboardViewModel } from "../data/viewModels/dashboardViewModel";

export function DashboardSurface({ data, liveStatus, loading, error, onRetry, onOpenQueue }) {
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
  const latestSyncCard = dashboard.healthCards.find((item) => item.key === "latest-sync");
  const hasAdminWork = dashboard.workItems.length > 0;
  const primaryKpis = [
    ...dashboard.statCards,
    latestSyncCard ? {
      key: "latest-sync-kpi",
      label: "Latest Sync",
      value: latestSyncCard.value,
      detail: latestSyncCard.detail,
      tone: latestSyncCard.tone,
      available: Boolean(latestSyncCard.value),
    } : null,
  ].filter(Boolean);

  return (
    <div className="surface-grid dashboard-surface dashboard-target-surface">
      <header className="dashboard-command-header">
        <div>
          <h2>{dashboard.title}</h2>
          <p>{dashboard.subtitle}</p>
        </div>
        <div className="dashboard-command-panel">
          <BeveledButton variant="secondary" icon="download" disabled title="Export requires a roster export endpoint.">Export Roster</BeveledButton>
          <BeveledButton variant="primary" icon="review" onClick={onOpenQueue}>Review Queue</BeveledButton>
        </div>
      </header>

      <section className="dashboard-kpi-row" aria-label="Dashboard metrics">
        {primaryKpis.map((card) => (
          <StatCard
            key={card.key}
            label={card.label}
            value={card.value}
            detail={card.detail}
            icon={dashboardIconForKey(card.key)}
            tone={card.tone}
            available={card.available}
            variant="hero"
            className={card.key === "latest-sync-kpi" ? "dashboard-latest-sync-card" : ""}
          />
        ))}
      </section>

      <section className="dashboard-target-operations" aria-label="Dashboard operations">
        <StonePanel
          title="Pending Admin Tasks"
          icon="scroll"
          variant="featured"
          className="dashboard-work-panel dashboard-primary-panel"
        >
          {hasAdminWork ? (
            <ul className="dashboard-work-grid">
              {dashboard.workItems.slice(0, 3).map((item) => (
                <li key={item.key} className="dashboard-work-card">
                  <IconGlyph name={workIconForTone(item.tone)} className="dashboard-work-icon" />
                  <div className="dashboard-work-copy">
                    <strong>{item.label}</strong>
                    <p>{item.detail || "No case detail available"}</p>
                  </div>
                  <StatusPill tone={item.tone}>{item.risk}</StatusPill>
                  <BeveledButton variant="secondary" icon="review" onClick={onOpenQueue}>Review</BeveledButton>
                </li>
              ))}
            </ul>
          ) : (
            <div className="dashboard-work-empty">
              <IconGlyph name="success" className="dashboard-clear-work-icon" />
              <div>
                <strong>No admin work waiting</strong>
                <p>The current dashboard feed has no open review cases.</p>
              </div>
              <BeveledButton variant="secondary" icon="review" onClick={onOpenQueue}>Open Queue</BeveledButton>
            </div>
          )}
        </StonePanel>

        <StonePanel title="Quick Tools" icon="tools" className="dashboard-tools-panel">
          <div className="dashboard-tools-list">
            <BeveledButton variant="primary" icon="review" onClick={onOpenQueue}>Review Queue</BeveledButton>
            <BeveledButton variant="secondary" icon="add-member" disabled title="Add Member requires a real member creation endpoint.">Add Member</BeveledButton>
            <BeveledButton variant="secondary" icon="refresh" disabled title="Sync HiScores requires a real app-facing sync action.">Sync HiScores</BeveledButton>
            <BeveledButton variant="secondary" icon="clean" disabled title="Run Audit requires a real audit endpoint.">Run Audit</BeveledButton>
          </div>
        </StonePanel>
      </section>

      <StonePanel title="Recent Clan Activity" icon="activity" variant="table" className="dashboard-activity-panel dashboard-primary-panel">
        <DataTable
          className="dashboard-activity-table"
          columns={[
            { key: "time", header: "Time" },
            { key: "event", header: "Event" },
            { key: "category", header: "Details" },
            { key: "status", header: "Status", render: (row) => <StatusPill tone={row.tone}>{row.category}</StatusPill> },
          ]}
          rows={dashboard.recentChanges}
          emptyTitle="No recent changes"
          emptyMessage="No meaningful clan changes were recorded in the current window."
        />
      </StonePanel>

      <footer className="dashboard-target-footer" aria-label="Dashboard footer">
        <span>Unofficial OSRS Clan Tracker</span>
        <span>Frontend uses existing tracker API data only</span>
      </footer>
    </div>
  );
}

function dashboardIconForKey(key) {
  const icons = {
    "tracked-members": "members",
    "pending-promotions": "promotion",
    "open-admin-cases": "review",
    "latest-sync-kpi": "user-refresh",
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
