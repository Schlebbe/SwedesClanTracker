import { useMemo, useState } from "react";
import { BeveledButton } from "../components/osrs/BeveledButton";
import { DataTable } from "../components/osrs/DataTable";
import { EmptyFeatureState } from "../components/osrs/EmptyFeatureState";
import { StatusPill } from "../components/osrs/StatusPill";
import { StonePanel } from "../components/osrs/StonePanel";
import { mapRosterToRosterViewModel } from "../data/viewModels/rosterViewModel";

export function MembersSurface({ rows, loading, error, onRetry, onOpenProfile }) {
  const [query, setQuery] = useState("");
  const [statusFilter, setStatusFilter] = useState("ALL");
  const roster = useMemo(() => mapRosterToRosterViewModel(rows), [rows]);
  const filtered = useMemo(() => {
    const needle = query.trim().toLowerCase();
    return roster.rows.filter((row) => (!needle || row.username.toLowerCase().includes(needle)) && (statusFilter === "ALL" || row.statusRaw === statusFilter));
  }, [query, roster.rows, statusFilter]);

  return (
    <div className="page members-page">
      <header className="page-header">
        <div>
          <h1>Clan members</h1>
          <p>Browse the current roster and inspect sync freshness.</p>
        </div>
        <StatusPill tone="info">{filtered.length.toLocaleString()} shown</StatusPill>
      </header>

      <StonePanel className="members-toolbar-panel" compact>
        <div className="members-toolbar">
          <label>
            <span>Search</span>
            <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search by RSN" aria-label="Search by RSN" />
          </label>
          <label>
            <span>Status</span>
            <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value)} aria-label="Status">
              <option value="ALL">All statuses</option>
              {roster.statusOptions.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
            </select>
          </label>
        </div>
      </StonePanel>

      {loading ? <StonePanel><StatusPill tone="info" loading>Loading members…</StatusPill></StonePanel> : null}
      {error ? <StonePanel tone="danger"><EmptyFeatureState title="Members could not be loaded" message={error} tone="danger" action={<BeveledButton variant="ghost" onClick={onRetry}>Retry</BeveledButton>} /></StonePanel> : null}

      {!loading && !error ? (
        <>
          <section className="summary-strip members-summary" aria-label="Roster summary">
            <SummaryItem label="Members" value={roster.summary.total} />
            <SummaryItem label="Stale sync" value={roster.summary.stale} tone={roster.summary.stale ? "warning" : "success"} />
            <SummaryItem label="Review cases" value={roster.summary.review} tone={roster.summary.review ? "warning" : "success"} />
            <SummaryItem label="Promotions" value={roster.summary.promotions} tone={roster.summary.promotions ? "warning" : "success"} />
            <SummaryItem label="Rank mismatch" value={roster.summary.rankMismatch} tone={roster.summary.rankMismatch ? "danger" : "success"} />
          </section>

          <StonePanel title="Roster" icon="members" className="members-table-panel">
            <DataTable
              className="members-table"
              columns={[
                { key: "username", header: "RSN", render: (row) => <span className="member-name" title={row.username}>{row.username}</span> },
                { key: "rank", header: "Clan rank" },
                { key: "status", header: "Status", render: (row) => <StatusPill tone={row.statusTone}>{row.statusLabel}</StatusPill> },
                { key: "lastSync", header: "Last synced" },
                { key: "lastSeen", header: "Last seen" },
                { key: "flags", header: "Flags", render: (row) => <div className="flag-list">{row.flags.map((flag) => <StatusPill key={flag.key} tone={flag.tone}>{flag.label}</StatusPill>)}</div> },
                { key: "actions", header: "Profile", render: (row) => <BeveledButton variant="ghost" icon="profile" disabled={!row.id} onClick={() => row.id && onOpenProfile(row.id)}>Open</BeveledButton> },
              ]}
              rows={filtered}
              getRowKey={(row) => row.id ?? row.username}
              emptyTitle="No members found"
              emptyMessage="No roster rows match the current filters."
              footer={<span>{roster.summary.total.toLocaleString()} members returned by the roster API.</span>}
            />
          </StonePanel>
        </>
      ) : null}
    </div>
  );
}

function SummaryItem({ label, value, tone = "info" }) {
  return <div className="summary-item"><span>{label}</span><strong className={`summary-value-${tone}`}>{value.toLocaleString()}</strong></div>;
}
