import { useEffect, useState } from "react";
import { IconGlyph } from "../components/osrs/IconGlyph";
import { ConsoleLink, EmptyState, ErrorState, Panel, PageFrame, State, StaleNotice, TimeStamp } from "../components/console/Primitives";
import { formatMetric, mapRosterToViewModel } from "../data/viewModels/consoleViewModels";
import { useConsoleRequest } from "../app/useConsoleRequest";
import { fetchConsoleRoster } from "../data/appDataApi";

export function ConsoleMembersSurface({ route, onNavigate }) {
  const queryString = route.query.toString();
  const searchParam = route.query.get("search") ?? "";
  const [search, setSearch] = useState(searchParam);
  useEffect(() => setSearch(searchParam), [searchParam]);
  const request = useConsoleRequest(`roster:${queryString}`, (signal) => fetchConsoleRoster(queryString, { signal }));
  const data = mapRosterToViewModel(request.data);

  function updateQuery(updates) {
    const query = new URLSearchParams(route.query);
    Object.entries(updates).forEach(([key, value]) => {
      if (value === undefined || value === null || value === "") query.delete(key);
      else query.set(key, value);
    });
    if (!query.has("page")) query.set("page", "1");
    onNavigate(`/members?${query.toString()}`);
  }

  return (
    <PageFrame
      eyebrow="Roster ledger"
      title="Members"
      description="Server-backed roster scanning with supported snapshot facts and explicit freshness signals."
      actions={<ConsoleLink href="/activity" onNavigate={onNavigate} className="outline-link"><IconGlyph name="activity" size={15} />Activity</ConsoleLink>}
    >
      {request.stale ? <StaleNotice /> : null}
      <Panel className="filter-panel" title="Roster filters" eyebrow={`${data.totalCount} records · 50 rows per page`} icon="filter">
        <form className="filter-form" onSubmit={(event) => { event.preventDefault(); updateQuery({ search, page: "1" }); }}>
          <label className="field field-search"><span>Search RSN</span><div className="input-with-icon"><IconGlyph name="search" size={16} /><input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Search long OSRS names…" /></div></label>
          <label className="field"><span>Status</span><select value={route.query.get("status") ?? ""} onChange={(event) => updateQuery({ status: event.target.value, page: "1" })}><option value="">All statuses</option><option value="ACTIVE">Active</option><option value="NEW_PENDING_REVIEW">New · pending review</option><option value="MISSING_PENDING_REVIEW">Missing · pending review</option><option value="MERGE_SUGGESTED">Merge suggested</option><option value="REMOVED_CONFIRMED">Removed</option></select></label>
          <label className="field"><span>Attention</span><select value={route.query.get("attention") ?? ""} onChange={(event) => updateQuery({ attention: event.target.value, page: "1" })}><option value="">All attention</option><option value="stale">Stale sync</option><option value="review">Review</option><option value="promotion">Promotion</option><option value="rank-mismatch">Rank mismatch</option></select></label>
          <label className="field"><span>Sort</span><select value={route.query.get("sort") ?? "username"} onChange={(event) => updateQuery({ sort: event.target.value, page: "1" })}><option value="username">Username</option><option value="lastsynced">Last synced</option><option value="lastseen">Last seen</option><option value="rank">Current rank</option></select></label>
          <button className="icon-button" type="submit" aria-label="Apply member search"><IconGlyph name="arrow-right" size={16} /></button>
        </form>
      </Panel>

      {request.loading && !request.data ? <Panel title="Members"><div className="table-state"><span className="loading-mark" />Loading roster rows…</div></Panel> : null}
      {request.error && !request.data ? <ErrorState error={request.error} onRetry={() => window.location.reload()} /> : null}
      {request.data && !request.error ? <Panel title="Current roster" eyebrow={`Page ${data.page} of ${data.totalPages}`} icon="members">
        {data.rows.length ? <div className="roster-table-wrap"><table className="roster-table"><caption className="sr-only">Clan members</caption><thead><tr><th scope="col">Member</th><th scope="col">Rank</th><th scope="col">Status</th><th scope="col">Latest snapshot</th><th scope="col">Freshness</th><th scope="col">Attention</th><th scope="col"><span className="sr-only">Open</span></th></tr></thead><tbody>{data.rows.map((row) => <RosterRow key={row.playerId} row={row} onNavigate={onNavigate} />)}</tbody></table><div className="roster-card-list">{data.rows.map((row) => <RosterCard key={row.playerId} row={row} onNavigate={onNavigate} />)}</div></div> : <EmptyState title="No matching members" detail="Try a different search, status, or attention filter." icon="search" />}
        <Pagination page={data.page} totalPages={data.totalPages} totalCount={data.totalCount} onChange={(page) => updateQuery({ page })} />
      </Panel> : null}
    </PageFrame>
  );
}

function RosterRow({ row, onNavigate }) {
  return <tr><td><ConsoleLink href={`/members/${row.playerId}`} onNavigate={onNavigate} className="member-cell"><span className="member-initial">{row.username?.slice(0, 1).toUpperCase() ?? "?"}</span><span><strong title={row.username}>{row.username}</strong><small>Seen <TimeStamp value={row.lastSeen} /></small></span></ConsoleLink></td><td><span className="rank-label">{row.currentRank}</span>{row.eligibleRank !== row.currentRank ? <small className="eligible-note">Eligible: {row.eligibleRank}</small> : null}</td><td><State value={row.status} /></td><td>{row.latestSnapshot ? <span className="snapshot-inline"><b>{formatMetric(row.latestSnapshot.totalLevel)}</b> total · {formatMetric(row.latestSnapshot.ehb, 1)} EHB · {formatMetric(row.latestSnapshot.ehp, 1)} EHP</span> : <span className="value-unknown">No snapshot</span>}</td><td><State value={row.freshness} label={row.freshness === "current" ? "Current" : row.freshness === "stale" ? "Stale" : "Unavailable"} /></td><td><AttentionFlags flags={row.attention} /></td><td><ConsoleLink href={`/members/${row.playerId}`} onNavigate={onNavigate} className="row-link" aria-label={`Open profile for ${row.username}`}><IconGlyph name="chevron-right" size={16} /></ConsoleLink></td></tr>;
}

function RosterCard({ row, onNavigate }) {
  return <article className="roster-card"><div className="roster-card-head"><ConsoleLink href={`/members/${row.playerId}`} onNavigate={onNavigate} className="member-cell"><span className="member-initial">{row.username?.slice(0, 1).toUpperCase() ?? "?"}</span><strong>{row.username}</strong></ConsoleLink><State value={row.status} /></div><div className="roster-card-facts"><span><small>Rank</small><b>{row.currentRank}</b></span><span><small>Snapshot</small><b>{row.latestSnapshot ? `${formatMetric(row.latestSnapshot.totalLevel)} total` : "Unavailable"}</b></span><span><small>Freshness</small><State value={row.freshness} label={row.freshness} /></span></div><div className="roster-card-foot"><AttentionFlags flags={row.attention} /><TimeStamp value={row.lastSynced} /></div></article>;
}

function AttentionFlags({ flags = [] }) {
  if (!flags.length) return <span className="value-unknown">Clear</span>;
  return <span className="attention-flags">{flags.map((flag) => <State key={flag} value={flag} label={flag === "rank-mismatch" ? "Rank mismatch" : flag} />)}</span>;
}

function Pagination({ page, totalPages, totalCount, onChange }) {
  if (totalPages <= 1) return <div className="pagination-summary">{totalCount} records</div>;
  return <div className="pagination"><span>{totalCount} records · page {page} of {totalPages}</span><span><button type="button" className="icon-button" disabled={page <= 1} onClick={() => onChange(page - 1)} aria-label="Previous page"><IconGlyph name="chevron-left" size={16} /></button><button type="button" className="icon-button" disabled={page >= totalPages} onClick={() => onChange(page + 1)} aria-label="Next page"><IconGlyph name="chevron-right" size={16} /></button></span></div>;
}
