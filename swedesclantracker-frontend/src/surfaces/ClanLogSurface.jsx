import { useMemo, useState } from "react";
import { BeveledButton } from "../components/osrs/BeveledButton";
import { DataTable } from "../components/osrs/DataTable";
import { EmptyFeatureState } from "../components/osrs/EmptyFeatureState";
import { StatusPill } from "../components/osrs/StatusPill";
import { StonePanel } from "../components/osrs/StonePanel";
import { filterActivityRows, mapClanLogToActivityLogViewModel } from "../data/viewModels/activityLogViewModel";

export function ClanLogSurface({ log, loading, error, onRetry }) {
  const [filter, setFilter] = useState("important");
  const activity = useMemo(() => mapClanLogToActivityLogViewModel(log), [log]);
  const rows = useMemo(() => filterActivityRows(activity, filter), [activity, filter]);
  const columns = useMemo(() => buildColumns(activity), [activity]);

  return (
    <div className="surface-grid activity-surface">
      <header className="surface-header activity-header">
        <div>
          <p className="eyebrow">Activity Log</p>
          <h2>{activity.title}</h2>
          <p>{activity.subtitle}</p>
        </div>
        <StatusPill tone={activity.summary.important ? "info" : "neutral"}>
          {activity.summary.important} important
        </StatusPill>
      </header>

      {loading ? (
        <StonePanel>
          <StatusPill tone="info" loading>Loading activity log...</StatusPill>
        </StonePanel>
      ) : null}

      {error ? (
        <StonePanel tone="danger">
          <EmptyFeatureState
            title="Unable to load activity log"
            message={error}
            tone="danger"
            action={<BeveledButton onClick={onRetry}>Retry</BeveledButton>}
          />
        </StonePanel>
      ) : null}

      {!loading && !error ? (
        <>
          <StonePanel className="activity-toolbar-panel">
            <div className="activity-toolbar" aria-label="Activity log filters">
              {activity.filters.map((item) => (
                <BeveledButton
                  key={item.id}
                  variant={filter === item.id ? "secondary" : "ghost"}
                  className={filter === item.id ? "activity-filter-active" : ""}
                  onClick={() => setFilter(item.id)}
                >
                  {item.label}
                </BeveledButton>
              ))}
            </div>
          </StonePanel>

          <StonePanel
            title="Clan Activity"
            subtitle="Real lifecycle projections from the current app API"
            actions={<StatusPill tone={rows.length ? "info" : "neutral"}>{rows.length} shown</StatusPill>}
            className="activity-table-panel"
          >
            <DataTable
              columns={columns}
              rows={rows}
              className="activity-table"
              emptyTitle="No activity events"
              emptyMessage="No clan-log rows match the current filter."
            />
          </StonePanel>

          {activity.summary.routine ? (
            <StonePanel title="Routine Bundle" subtitle="Condensed sync and system entries from the same API response">
              <p className="activity-routine-note">
                {activity.summary.routine} routine entries are included under Sync/System and All.
              </p>
            </StonePanel>
          ) : null}
        </>
      ) : null}
    </div>
  );
}

function buildColumns(activity) {
  const columns = [
    {
      key: "time",
      header: "Time",
      render: (row) => <span className="activity-time">{row.time}</span>,
    },
    {
      key: "event",
      header: "Event",
      render: (row) => (
        <div className="activity-event-cell">
          <strong>{row.title}</strong>
          <span>{row.typeLabel}</span>
        </div>
      ),
    },
  ];

  if (activity.hasMemberColumn) {
    columns.push({
      key: "member",
      header: "Member",
      render: (row) => row.member ? <span className="activity-member">{row.member}</span> : "Not provided",
    });
  }

  columns.push(
    {
      key: "detail",
      header: "Details",
      render: (row) => <span className="activity-detail">{row.detail}</span>,
    },
    {
      key: "status",
      header: "Status",
      render: (row) => <StatusPill tone={row.tone}>{row.statusLabel}</StatusPill>,
    }
  );

  if (activity.hasActionColumn) {
    columns.push({
      key: "action",
      header: "Action",
      render: (row) => row.action ? (
        <a className="osrs-button osrs-button-ghost activity-action-link" href={row.action.target}>
          {row.action.label}
        </a>
      ) : "None",
    });
  }

  return columns;
}
