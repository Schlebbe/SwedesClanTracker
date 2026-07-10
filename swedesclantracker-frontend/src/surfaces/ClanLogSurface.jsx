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
    <div className="page activity-page">
      <header className="page-header">
        <div><h1>Activity log</h1><p>Recent lifecycle and tracker events.</p></div>
        <StatusPill tone={activity.summary.important ? "info" : "neutral"}>{activity.summary.important.toLocaleString()} important</StatusPill>
      </header>

      {loading ? <StonePanel><StatusPill tone="info" loading>Loading activity…</StatusPill></StonePanel> : null}
      {error ? <StonePanel tone="danger"><EmptyFeatureState title="Activity could not be loaded" message={error} tone="danger" action={<BeveledButton variant="ghost" onClick={onRetry}>Retry</BeveledButton>} /></StonePanel> : null}
      {!loading && !error ? <>
        <StonePanel className="activity-filter-panel" compact>
          <div className="filter-row" aria-label="Activity filters">{activity.filters.map((item) => <BeveledButton key={item.id} variant={filter === item.id ? "secondary" : "ghost"} onClick={() => setFilter(item.id)}>{item.label}</BeveledButton>)}</div>
        </StonePanel>
        <StonePanel title="Events" icon="activity" actions={<StatusPill tone="neutral">{rows.length.toLocaleString()} shown</StatusPill>}>
          <DataTable columns={columns} rows={rows} className="activity-table" emptyTitle="No activity events" emptyMessage="No events match the selected filter." />
        </StonePanel>
      </> : null}
    </div>
  );
}

function buildColumns(activity) {
  const columns = [
    { key: "time", header: "Time", render: (row) => <span className="activity-time">{row.time}</span> },
    { key: "event", header: "Event", render: (row) => <div className="event-cell"><strong>{row.title}</strong><span>{row.typeLabel}</span></div> },
  ];

  if (activity.hasMemberColumn) columns.push({ key: "member", header: "Member", render: (row) => row.member || "Unavailable" });
  columns.push(
    { key: "detail", header: "Details", render: (row) => <span className="activity-detail">{row.detail}</span> },
    { key: "status", header: "Status", render: (row) => <StatusPill tone={row.tone}>{row.statusLabel}</StatusPill> },
  );
  if (activity.hasActionColumn) columns.push({ key: "action", header: "Action", render: (row) => row.action ? <a className="activity-action-link" href={row.action.target}>{row.action.label}</a> : "Unavailable" });
  return columns;
}
