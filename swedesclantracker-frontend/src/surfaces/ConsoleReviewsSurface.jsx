import { useState } from "react";
import { IconGlyph } from "../components/osrs/IconGlyph";
import { ConsoleLink, DisabledActionList, EmptyState, ErrorState, EvidenceList, Panel, PageFrame, ReadOnlyNotice, State, StaleNotice, TimeStamp } from "../components/console/Primitives";
import { humanize, mapReviewDetailToViewModel, mapReviewsToViewModel, stateTone } from "../data/viewModels/consoleViewModels";
import { useConsoleRequest } from "../app/useConsoleRequest";
import { fetchConsoleReviewDetail, fetchConsoleReviews, fetchIgnoredPolicies } from "../data/appDataApi";

export function ConsoleReviewsQueueSurface({ route, onNavigate }) {
  const queryString = route.query.toString();
  const [search, setSearch] = useState(route.query.get("search") ?? "");
  const request = useConsoleRequest(`reviews:${queryString}`, (signal) => fetchConsoleReviews(queryString, { signal }));
  const ignoredRequest = useConsoleRequest("ignored-policies", (signal) => fetchIgnoredPolicies({ signal }));
  const data = mapReviewsToViewModel(request.data);
  const ignored = ignoredRequest.data?.items ?? [];

  function updateQuery(updates) {
    const query = new URLSearchParams(route.query);
    Object.entries(updates).forEach(([key, value]) => {
      if (value === undefined || value === null || value === "") query.delete(key);
      else query.set(key, value);
    });
    onNavigate(`/reviews${query.toString() ? `?${query.toString()}` : ""}`);
  }

  return (
    <PageFrame eyebrow="Review docket" title="Reviews" description="Typed, factual review projections with Discord-parity decision direction shown as non-interactive descriptors." actions={<ConsoleLink href="/activity" onNavigate={onNavigate} className="outline-link"><IconGlyph name="activity" size={15} />Activity</ConsoleLink>}>
      <ReadOnlyNotice />
      {request.stale ? <StaleNotice /> : null}
      <Panel className="filter-panel" title="Review filters" eyebrow={`${data.totalCount} projected cases`} icon="filter">
        <form className="filter-form review-filter-form" onSubmit={(event) => { event.preventDefault(); updateQuery({ search, page: "1" }); }}>
          <label className="field field-search"><span>Search subject or summary</span><div className="input-with-icon"><IconGlyph name="search" size={16} /><input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="RSN or factual signal…" /></div></label>
          <label className="field"><span>Family</span><select value={route.query.get("family") ?? ""} onChange={(event) => updateQuery({ family: event.target.value, page: "1" })}><option value="">All families</option>{data.lanes.map((lane) => <option value={lane.family} key={lane.family}>{lane.label}</option>)}</select></label>
          <label className="field"><span>State</span><select value={route.query.get("state") ?? ""} onChange={(event) => updateQuery({ state: event.target.value, page: "1" })}><option value="">All states</option><option value="open">Open</option><option value="processing">Processing</option><option value="conflict">Conflict</option><option value="superseded">Superseded</option></select></label>
          <button className="icon-button" type="submit" aria-label="Apply review search"><IconGlyph name="arrow-right" size={16} /></button>
        </form>
      </Panel>

      {request.loading && !request.data ? <Panel title="Review queue"><div className="table-state"><span className="loading-mark" />Loading typed cases…</div></Panel> : null}
      {request.error && !request.data ? <ErrorState error={request.error} onRetry={() => window.location.reload()} /> : null}
      {request.data && !request.error ? <div className="review-layout"><Panel title="Decision lanes" eyebrow="Read-only queue summary" icon="list"><div className="review-lanes">{data.lanes.map((lane) => <ConsoleLink key={lane.family} href={`/reviews?family=${encodeURIComponent(lane.family)}`} onNavigate={onNavigate} className="review-lane"><span className={`lane-marker lane-marker-${lane.tone}`} /><span><strong>{lane.label}</strong><small>{lane.count} projected cases</small></span><b>{lane.count}</b></ConsoleLink>)}</div>{!data.lanes.length ? <EmptyState title="No typed review cases" detail="The API returned no active or informational review projections." icon="check" /> : null}</Panel><Panel title="Open work" eyebrow={`Page ${data.page} of ${data.totalPages}`} icon="review"><div className="review-case-list">{data.items.length ? data.items.map((item) => <ReviewCaseRow key={item.caseId} item={item} onNavigate={onNavigate} />) : <EmptyState title="No matching cases" detail="Change the family, state, or search filters." icon="search" />}</div><Pagination page={data.page} totalPages={data.totalPages} onChange={(page) => updateQuery({ page })} /></Panel></div> : null}

      <Panel title="Ignored policies" eyebrow="Informational persisted policy state" icon="shield" className="ignored-panel">
        {ignoredRequest.error ? <div className="inline-warning">Ignored policy view unavailable: {ignoredRequest.error.message}</div> : ignored.length ? <div className="ignored-list">{ignored.map((item) => <div className="ignored-row" key={item.policyId}><State value="ignored" label="Ignored" /><span><strong>{item.subject}</strong><small>{humanize(item.family)} · <TimeStamp value={item.ignoredAt} /></small></span><span className="disabled-inline">Resume descriptor unavailable</span></div>)}</div> : <EmptyState title="No active ignored policies" detail="No open WOM-only or rank-mismatch ignore events are stored." icon="shield" />}
      </Panel>
    </PageFrame>
  );
}

export function ConsoleReviewDetailSurface({ route, onNavigate }) {
  const request = useConsoleRequest(`review-detail:${route.caseId}`, (signal) => fetchConsoleReviewDetail(route.caseId, { signal }));
  const detail = mapReviewDetailToViewModel(request.data);
  if (request.loading && !detail) return <PageFrame eyebrow="Review case" title="Loading case…"><div className="loading-grid"><div /><div /><div /></div></PageFrame>;
  if (request.error && !detail) return <PageFrame eyebrow="Review case" title="Case unavailable"><ErrorState error={request.error} onRetry={() => window.location.reload()} /></PageFrame>;
  if (!detail) return <PageFrame eyebrow="Review case" title="Case not found"><EmptyState title="Unknown case" detail="The opaque case ID is not present in the current read-only projection." icon="search" /></PageFrame>;
  const item = detail.case;
  return <PageFrame eyebrow={`${humanize(item.family)} · ${item.caseId}`} title={item.subject} description={item.summary} actions={<ConsoleLink href="/reviews" onNavigate={onNavigate} className="outline-link"><IconGlyph name="arrow-left" size={15} />Back to reviews</ConsoleLink>}>
    <ReadOnlyNotice />
    <div className="case-status-bar"><State value={item.state} /><span>Opened <TimeStamp value={item.openedAt} /></span><span>Observed <TimeStamp value={item.observedAt} /></span><span>Source: {item.source ?? "Unavailable"}</span></div>
    {item.state === "conflict" ? <div className="warning-banner warning-danger" role="alert"><IconGlyph name="alert" size={18} /><span>Conflicting persisted signals require recheck. No action can be taken from this read-only page.</span></div> : null}
    <div className="case-grid"><Panel title="Evidence" eyebrow="Persisted facts and supported observations" icon="checklist"><EvidenceList evidence={item.evidence} />{item.observations.length ? <div className="observation-list">{item.observations.map((observation) => <p key={observation}><IconGlyph name="info" size={15} />{observation}</p>)}</div> : null}</Panel><Panel title="Warnings" eyebrow="Known boundaries" icon="alert">{item.warnings.length ? <ul className="warning-list">{item.warnings.map((warning) => <li key={warning}>{warning}</li>)}</ul> : <EmptyState title="No warnings recorded" detail="The projection returned no additional warning text." icon="check" />}<div className="case-architecture-note"><strong>Gate state</strong><span>{humanize(detail.gateState)}</span><small>Case version: {item.caseVersion ?? "Unavailable in current schema"}</small></div></Panel><Panel title="Decision direction" eyebrow="Exact Discord-parity labels · disabled" icon="review"><DisabledActionList actions={item.actions} /></Panel><Panel title="Related work" eyebrow="Precedence and suppression" icon="history">{detail.suppressedSignals.length ? <ul className="warning-list">{detail.suppressedSignals.map((signal) => <li key={signal}>{signal}</li>)}</ul> : null}{item.relatedCases.length || item.supersededCases.length ? <div className="related-case-list">{[...item.relatedCases, ...item.supersededCases].map((related) => <ConsoleLink key={`${related.caseId}-${related.relationship}`} href={`/reviews/${encodeURIComponent(related.caseId)}`} onNavigate={onNavigate} className="related-case-row"><span>{humanize(related.family)}</span><strong>{related.subject}</strong><State value={related.relationship} label={related.relationship} /><IconGlyph name="chevron-right" size={15} /></ConsoleLink>)}</div> : <EmptyState title="No related cases" detail="No additional persisted case relationship is derivable." icon="history" />}</Panel></div>
  </PageFrame>;
}

function ReviewCaseRow({ item, onNavigate }) {
  return <ConsoleLink className="review-case-row" href={`/reviews/${encodeURIComponent(item.caseId)}`} onNavigate={onNavigate}><div className={`case-family-bar case-family-${stateTone(item.family)}`} /><div className="review-case-main"><div className="review-case-title"><strong>{item.subject}</strong><State value={item.state} /></div><span>{item.summary}</span><small><TimeStamp value={item.openedAt} /> · {item.source ?? "Source unavailable"}</small></div><div className="review-case-evidence">{item.evidence?.slice(0, 2).map((evidence) => <span key={evidence.label}><b>{evidence.label}</b>{evidence.value ?? "Unavailable"}</span>)}</div><IconGlyph name="chevron-right" size={17} /></ConsoleLink>;
}

function Pagination({ page, totalPages, onChange }) {
  if (totalPages <= 1) return null;
  return <div className="pagination"><span>Page {page} of {totalPages}</span><span><button type="button" className="icon-button" disabled={page <= 1} onClick={() => onChange(page - 1)} aria-label="Previous page"><IconGlyph name="chevron-left" size={16} /></button><button type="button" className="icon-button" disabled={page >= totalPages} onClick={() => onChange(page + 1)} aria-label="Next page"><IconGlyph name="chevron-right" size={16} /></button></span></div>;
}
