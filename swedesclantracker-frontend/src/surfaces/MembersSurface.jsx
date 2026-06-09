import { useMemo, useState } from "react";
import { BeveledButton } from "../components/osrs/BeveledButton";
import { DataTable } from "../components/osrs/DataTable";
import { StatusPill } from "../components/osrs/StatusPill";
import { StonePanel } from "../components/osrs/StonePanel";
import { mapRosterToRosterViewModel } from "../data/viewModels/rosterViewModel";

export function MembersSurface({ rows, loading, error, onRetry, onOpenProfile }) {
  const [query, setQuery] = useState("");
  const [statusFilter, setStatusFilter] = useState("ALL");
  const roster = useMemo(() => mapRosterToRosterViewModel(rows), [rows]);

  const filtered = useMemo(() => {
    const needle = query.trim().toLowerCase();

    return roster.rows.filter((row) => {
      const queryMatch = !needle || row.username.toLowerCase().includes(needle);
      const statusMatch = statusFilter === "ALL" || row.statusRaw === statusFilter;
      return queryMatch && statusMatch;
    });
  }, [query, roster.rows, statusFilter]);

  return (
    <div className="surface-grid members-surface">
      <header className="surface-header members-header">
        <div>
          <p className="eyebrow">Members</p>
          <h2>{roster.title}</h2>
          <p>{roster.subtitle}</p>
        </div>
      </header>

      <StonePanel>
        <div className="members-toolbar">
          <input
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Search by RSN"
            aria-label="Search by RSN"
          />
          <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value)} aria-label="Status">
            <option value="ALL">All status</option>
            {roster.statusOptions.map((option) => (
              <option key={option.value} value={option.value}>{option.label}</option>
            ))}
          </select>
        </div>
      </StonePanel>

      {loading ? (
        <StonePanel>
          <StatusPill tone="info" loading>Loading roster...</StatusPill>
        </StonePanel>
      ) : null}

      {error ? (
        <StonePanel tone="danger">
          <StatusPill tone="danger">Unable to load roster: {error}</StatusPill>
          <div className="message-action">
            <BeveledButton variant="ghost" onClick={onRetry}>Retry</BeveledButton>
          </div>
        </StonePanel>
      ) : null}

      {!loading && !error ? (
        <>
          <section className="members-summary" aria-label="Roster summary">
            <SummaryItem label="Members" value={roster.summary.total} tone="info" state="loaded" />
            <SummaryItem label="Stale Sync" value={roster.summary.stale} tone={roster.summary.stale > 0 ? "warning" : "success"} state={roster.summary.stale > 0 ? "watch" : "clear"} />
            <SummaryItem label="Review Cases" value={roster.summary.review} tone={roster.summary.review > 0 ? "warning" : "success"} state={roster.summary.review > 0 ? "review" : "clear"} />
            <SummaryItem label="Promotions" value={roster.summary.promotions} tone="success" state="ready" />
            <SummaryItem label="Rank Mismatch" value={roster.summary.rankMismatch} tone={roster.summary.rankMismatch > 0 ? "danger" : "success"} state={roster.summary.rankMismatch > 0 ? "fix" : "clear"} />
          </section>

          <StonePanel title="Roster" subtitle={`${filtered.length.toLocaleString()} of ${roster.summary.total.toLocaleString()} members shown`}>
            <DataTable
              className="members-table"
              columns={[
                {
                  key: "username",
                  header: "RSN",
                  render: (row) => <span className="members-username" title={row.username}>{row.username}</span>,
                },
                { key: "rank", header: "Clan Rank" },
                {
                  key: "status",
                  header: "Status",
                  render: (row) => <StatusPill tone={row.statusTone}>{row.statusLabel}</StatusPill>,
                },
                {
                  key: "lastSync",
                  header: "Last Sync",
                  render: (row) => (
                    <div className="members-date-cell">
                      <span>{row.lastSync}</span>
                      {row.isSyncStale ? <StatusPill tone="warning">stale</StatusPill> : null}
                    </div>
                  ),
                },
                { key: "lastSeen", header: "Last Seen" },
                {
                  key: "flags",
                  header: "Flags",
                  render: (row) => (
                    <div className="flags-inline">
                      {row.flags.map((flag) => (
                        <StatusPill key={flag.key} tone={flag.tone}>{flag.label}</StatusPill>
                      ))}
                    </div>
                  ),
                },
                {
                  key: "actions",
                  header: "Actions",
                  render: (row) => <BeveledButton variant="ghost" onClick={() => onOpenProfile(row.id)}>Open</BeveledButton>,
                },
              ]}
              rows={filtered}
              emptyTitle="No members found"
              emptyMessage="No roster rows match the current search and status filter."
            />
          </StonePanel>
        </>
      ) : null}
    </div>
  );
}

function SummaryItem({ label, value, tone, state }) {
  return (
    <div className="members-summary-item">
      <span>{label}</span>
      <strong>{value.toLocaleString()}</strong>
      <StatusPill tone={tone}>{state}</StatusPill>
    </div>
  );
}
