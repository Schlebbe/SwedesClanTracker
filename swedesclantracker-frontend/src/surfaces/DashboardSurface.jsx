import { BeveledButton } from "../components/osrs/BeveledButton";
import { DataTable } from "../components/osrs/DataTable";
import { IconGlyph } from "../components/osrs/IconGlyph";
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

  return (
    <div className="dashboard-target-surface" data-dashboard-visual-target="Dashboard.png">
      <header className="dashboard-hero-frame">
        <div className="dashboard-title-block">
          <h2>{dashboard.title}</h2>
          <p>{dashboard.subtitle}</p>
        </div>
        <div className="dashboard-hero-actions" aria-label="Dashboard actions">
          <BeveledButton variant="secondary" icon="download" disabled title="Export requires a roster export endpoint.">
            Export Roster
          </BeveledButton>
          <BeveledButton variant="primary" icon="refresh" disabled title="Update Roster requires a safe app-facing sync trigger.">
            Update Roster
          </BeveledButton>
        </div>
      </header>

      <section className="dashboard-kpi-row" aria-label="Dashboard metrics">
        {dashboard.kpis.map((card) => (
          <DashboardKpiCard key={card.key} card={card} />
        ))}
      </section>

      <section className="dashboard-operations-grid" aria-label="Dashboard operations">
        <StonePanel
          title="Pending Admin Tasks"
          icon="scroll"
          variant="featured"
          className="dashboard-work-panel dashboard-reference-panel"
        >
          <ul className="dashboard-task-grid">
            {dashboard.adminTasks.map((task) => (
              <DashboardTaskCard key={task.key} task={task} onOpenQueue={onOpenQueue} />
            ))}
          </ul>
        </StonePanel>

        <StonePanel title="Quick Tools" icon="tools" className="dashboard-tools-panel dashboard-reference-panel">
          <div className="dashboard-tools-list">
            {dashboard.quickTools.map((tool) => (
              <BeveledButton
                key={tool.key}
                variant="secondary"
                icon={tool.icon}
                disabled
                title={tool.reason}
                className="dashboard-tool-button"
              >
                {tool.label}
              </BeveledButton>
            ))}
          </div>
        </StonePanel>
      </section>

      <StonePanel
        title="Recent Clan Activity"
        icon="activity"
        variant="table"
        className="dashboard-activity-panel dashboard-reference-panel"
        footer={<button type="button" className="dashboard-table-link" disabled>View full activity log</button>}
      >
        <DataTable
          className="dashboard-activity-table"
          columns={[
            { key: "time", header: "Time" },
            { key: "event", header: "Event", render: (row) => <EventCell row={row} /> },
            { key: "member", header: "Member", render: (row) => <span className="dashboard-member-cell">{row.member}</span> },
            { key: "detail", header: "Details", render: (row) => <span className="dashboard-detail-cell">{row.detail}</span> },
            { key: "status", header: "Status", render: (row) => <StatusPill tone={row.tone}>{row.status}</StatusPill> },
            { key: "admin", header: "Admin", render: (row) => <button type="button" className="dashboard-row-action" disabled>{row.action}</button> },
          ]}
          rows={dashboard.activityRows}
          getRowKey={(row) => row.key}
          emptyTitle="No recent changes"
          emptyMessage="No meaningful clan changes were recorded in the current window."
        />
      </StonePanel>

      <footer className="dashboard-target-footer" aria-label="Dashboard footer">
        {dashboard.footerItems.map((item) => (
          <span key={item}>{item}</span>
        ))}
        <IconGlyph name="rank" className="dashboard-footer-mark" />
      </footer>
    </div>
  );
}

function DashboardKpiCard({ card }) {
  return (
    <article className={`dashboard-kpi-card dashboard-kpi-card-${card.tone}`} data-source={card.source}>
      <header>
        <IconGlyph name={card.icon} className="dashboard-kpi-icon" />
        <span>{card.label}</span>
      </header>
      <strong>{card.value}</strong>
      {card.detail ? <p>{card.detail}</p> : null}
      {card.trend ? <small>{card.trend}</small> : null}
      {card.source === "placeholder" ? (
        <span className="dashboard-source-note" title={card.unavailableReason}>Visual placeholder</span>
      ) : null}
    </article>
  );
}

function DashboardTaskCard({ task, onOpenQueue }) {
  return (
    <li className={`dashboard-task-card dashboard-task-card-${task.tone}`} data-source={task.source}>
      <IconGlyph name={task.icon} className="dashboard-task-icon" />
      <strong>{task.label}</strong>
      <span className="dashboard-task-count">{task.count}</span>
      <p>{task.detail}</p>
      <BeveledButton variant="secondary" icon="review" onClick={onOpenQueue}>Review</BeveledButton>
    </li>
  );
}

function EventCell({ row }) {
  return (
    <span className="dashboard-event-cell">
      <IconGlyph name={row.icon} className="dashboard-event-icon" />
      <span>{row.event}</span>
    </span>
  );
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
