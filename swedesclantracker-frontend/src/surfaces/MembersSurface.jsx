import { useMemo, useState } from "react";
import { toneClass } from "../ui";

export function MembersSurface({ rows, loading, error, onRetry, onOpenProfile }) {
  const [query, setQuery] = useState("");
  const [statusFilter, setStatusFilter] = useState("ALL");

  const filtered = useMemo(() => {
    return rows.filter((row) => {
      const queryMatch = row.username.toLowerCase().includes(query.toLowerCase());
      const statusMatch = statusFilter === "ALL" || row.status === statusFilter;
      return queryMatch && statusMatch;
    });
  }, [query, rows, statusFilter]);

  return (
    <div className="surface-grid">
      <header className="surface-header">
        <p className="eyebrow">Members / Roster Explorer</p>
        <h2>Rich Roster Browsing with Profile Entry Points</h2>
        <p>Use search and facets for scanning, then jump into player context quickly.</p>
      </header>

      <section className="panel toolbar">
        <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search username" aria-label="Search username" />
        <select value={statusFilter} onChange={(event) => setStatusFilter(event.target.value)} aria-label="Status">
          <option value="ALL">All status</option>
          <option value="ACTIVE">Active</option>
          <option value="NEW_PENDING_REVIEW">New pending review</option>
          <option value="MISSING_PENDING_REVIEW">Missing pending review</option>
          <option value="MERGE_SUGGESTED">Merge suggested</option>
        </select>
      </section>

      {loading ? <section className="panel"><p className="tone tone-info" data-loading="true">Loading roster...</p></section> : null}

      {error ? (
        <section className="panel">
          <p className="tone tone-danger">Unable to load roster: {error}</p>
          <div className="message-action"><button className="btn-ghost" onClick={onRetry}>Retry</button></div>
        </section>
      ) : null}

      {!loading && !error ? (
        <section className="panel table-wrap" aria-label="Roster scan results">
          <table className="roster-table">
            <thead>
              <tr>
                <th>Username</th>
                <th>Rank</th>
                <th>Status</th>
                <th>Last sync</th>
                <th>Flags</th>
                <th>Profile</th>
              </tr>
            </thead>
            <tbody>
              {filtered.length ? filtered.map((row) => (
                <tr key={row.id}>
                  <td className="username-cell" title={row.username}><span className="username-text">{row.username}</span></td>
                  <td>{row.rank ?? "unknown"}</td>
                  <td><span className={toneClass(row.status?.includes("REVIEW") || row.status?.includes("MERGE") ? "warning" : "success")}>{row.status ?? "unknown"}</span></td>
                  <td>
                    {row.lastSync ? new Date(row.lastSync).toLocaleString("sv-SE") : "unknown"}
                    {row.isSyncStale ? <span className={toneClass("warning")}> stale</span> : null}
                  </td>
                  <td>
                    <div className="flags-inline">
                      {row.hasOpenReviewCase ? <span className={toneClass("warning")}>review</span> : null}
                      {row.hasPendingPromotion ? <span className={toneClass("success")}>promotion</span> : null}
                      {row.hasRankMismatch ? <span className={toneClass("danger")}>mismatch</span> : null}
                      {!row.hasOpenReviewCase && !row.hasPendingPromotion && !row.hasRankMismatch ? <span className={toneClass("info")}>clear</span> : null}
                    </div>
                  </td>
                  <td><button className="btn-ghost" onClick={() => onOpenProfile(row.id)}>Open</button></td>
                </tr>
              )) : (
                <tr><td colSpan={6} className="table-empty">No members match this search and status filter.</td></tr>
              )}
            </tbody>
          </table>
        </section>
      ) : null}
    </div>
  );
}
