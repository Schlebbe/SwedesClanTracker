import { useState } from "react";
import { IconGlyph } from "../components/osrs/IconGlyph";
import { ConsoleLink, EmptyState, ErrorState, Panel, PageFrame, State, StaleNotice, TimeStamp } from "../components/console/Primitives";
import { humanize, mapActivityToViewModel } from "../data/viewModels/consoleViewModels";
import { useConsoleRequest } from "../app/useConsoleRequest";
import { fetchConsoleActivity } from "../data/appDataApi";

export function ConsoleActivitySurface({ route, onNavigate }) {
  const queryString = route.query.toString();
  const [player, setPlayer] = useState(route.query.get("player") ?? "");
  const request = useConsoleRequest(`activity:${queryString}`, (signal) => fetchConsoleActivity(queryString, { signal }));
  const data = mapActivityToViewModel(request.data);

  function updateQuery(updates) {
    const query = new URLSearchParams(route.query);
    Object.entries(updates).forEach(([key, value]) => {
      if (value === undefined || value === null || value === "") query.delete(key);
      else query.set(key, value);
    });
    onNavigate(`/activity${query.toString() ? `?${query.toString()}` : ""}`);
  }

  return <PageFrame eyebrow="Activity log" title="Activity" description="Chronological lifecycle facts with optional event, status, player, source, and date filters." actions={<ConsoleLink href="/" onNavigate={onNavigate} className="outline-link"><IconGlyph name="dashboard" size={15} />Overview</ConsoleLink>}>
    {request.stale ? <StaleNotice /> : null}
    <Panel className="filter-panel" title="Activity filters" eyebrow="No audit fields are invented" icon="filter">
      <form className="filter-form activity-filter-form" onSubmit={(event) => { event.preventDefault(); updateQuery({ player, page: undefined }); }}>
        <label className="field field-search"><span>Player or PlayerId</span><div className="input-with-icon"><IconGlyph name="search" size={16} /><input value={player} onChange={(event) => setPlayer(event.target.value)} placeholder="RSN or numeric ID…" /></div></label>
        <label className="field"><span>Event type</span><select value={route.query.get("eventType") ?? ""} onChange={(event) => updateQuery({ eventType: event.target.value })}><option value="">All event types</option>{data.eventTypes.map((eventType) => <option value={eventType} key={eventType}>{humanize(eventType)}</option>)}</select></label>
        <label className="field"><span>Status</span><select value={route.query.get("status") ?? ""} onChange={(event) => updateQuery({ status: event.target.value })}><option value="">All statuses</option>{data.statuses.map((status) => <option value={status} key={status}>{humanize(status)}</option>)}</select></label>
        <label className="field"><span>From</span><input type="date" value={route.query.get("from") ?? ""} onChange={(event) => updateQuery({ from: event.target.value })} /></label>
        <label className="field"><span>To</span><input type="date" value={route.query.get("to") ?? ""} onChange={(event) => updateQuery({ to: event.target.value })} /></label>
        <button className="icon-button" type="submit" aria-label="Apply activity filters"><IconGlyph name="arrow-right" size={16} /></button>
      </form>
    </Panel>
    {request.loading && !request.data ? <Panel title="Activity"><div className="table-state"><span className="loading-mark" />Loading lifecycle events…</div></Panel> : null}
    {request.error && !request.data ? <ErrorState error={request.error} onRetry={() => window.location.reload()} /> : null}
    {request.data && !request.error ? <Panel title="Lifecycle events" eyebrow={`${data.totalCount} returned events`} icon="history">{data.items.length ? <div className="activity-list">{data.items.map((item) => <ActivityRow key={item.eventId} item={item} onNavigate={onNavigate} />)}</div> : <EmptyState title="No matching activity" detail="No lifecycle events match the current filters." icon="search" />}</Panel> : null}
  </PageFrame>;
}

function ActivityRow({ item, onNavigate }) {
  return <article className="activity-row"><div className="activity-row-mark"><IconGlyph name={item.eventType?.includes("MISMATCH") ? "rank-mismatch" : item.eventType?.includes("PROMOTION") ? "badge" : "activity"} size={16} /></div><div className="activity-row-main"><div className="activity-row-head"><strong>{item.label}</strong><State value={item.status} /></div><p>{item.evidence?.find((evidence) => evidence.label === "Observation")?.value ?? "Observation unavailable"}</p><div className="activity-row-meta"><span>{item.playerName ? <ConsoleLink href={`/members/${item.playerId}`} onNavigate={onNavigate}>{item.playerName}</ConsoleLink> : "Anchor-only event"}</span><span>{item.source ?? "Source unavailable"}</span><span>Event #{item.eventId}</span>{item.correlationId ? <span>Correlation {item.correlationId}</span> : null}</div></div><TimeStamp value={item.occurredAt} />{item.caseId ? <ConsoleLink href={`/reviews/${encodeURIComponent(item.caseId)}`} onNavigate={onNavigate} className="row-link" aria-label="Open related review case"><IconGlyph name="chevron-right" size={16} /></ConsoleLink> : null}</article>;
}
