import { IconGlyph } from "../components/osrs/IconGlyph";
import { ConsoleLink, EmptyState, ErrorState, Metric, Panel, PageFrame, State, StaleNotice, TimeStamp } from "../components/console/Primitives";
import { formatMetric, mapProfileToViewModel } from "../data/viewModels/consoleViewModels";
import { useConsoleRequest } from "../app/useConsoleRequest";
import { fetchConsoleProfile } from "../data/appDataApi";

export function ConsoleProfileSurface({ route, onNavigate }) {
  const range = route.query.get("range") ?? "90d";
  const request = useConsoleRequest(`profile:${route.playerId}:${range}`, (signal) => fetchConsoleProfile(route.playerId, range, { signal }));
  const profile = mapProfileToViewModel(request.data);

  function setRange(nextRange) {
    onNavigate(`/members/${route.playerId}?range=${encodeURIComponent(nextRange)}`);
  }

  if (request.loading && !profile) return <PageFrame eyebrow="Member profile" title="Loading profile…"><div className="loading-grid"><div /><div /><div /></div></PageFrame>;
  if (request.error && !profile) return <PageFrame eyebrow="Member profile" title="Profile unavailable"><ErrorState error={request.error} onRetry={() => window.location.reload()} /></PageFrame>;
  if (!profile) return <PageFrame eyebrow="Member profile" title="Profile unavailable"><EmptyState title="Player not found" detail="The requested player ID is not present in the current repository." icon="profile" /></PageFrame>;

  const snapshot = profile.latestSnapshot;
  return (
    <PageFrame eyebrow="Member profile" title={profile.username} description="A persisted identity record with current rank, supported snapshot facts, freshness, and lifecycle history." actions={<ConsoleLink href="/members" onNavigate={onNavigate} className="outline-link"><IconGlyph name="arrow-left" size={15} />Back to members</ConsoleLink>}>
      {request.stale ? <StaleNotice /> : null}
      <section className="profile-identity">
        <div className="profile-identity-mark">{profile.username.slice(0, 2).toUpperCase()}</div>
        <div><span className="eyebrow">Tracked identity</span><h2>{profile.username}</h2><div className="profile-status-line"><State value={profile.status} /> <span>Current rank <b>{profile.currentRank}</b></span><span>Eligible rank <b>{profile.eligibleRank}</b></span><State value={profile.freshness} label={profile.freshness === "current" ? "Current sync" : profile.freshness === "stale" ? "Stale sync" : "Sync unavailable"} /></div></div>
      </section>

      <section className="metric-strip profile-metrics" aria-label="Supported player metrics">
        <Metric label="Total level" value={snapshot ? formatMetric(snapshot.totalLevel) : "Unavailable"} detail={snapshot ? `Observed ${snapshot.timestamp}` : "No PlayerSnapshot persisted"} tone={snapshot ? "info" : "neutral"} icon="stats" />
        <Metric label="EHB" value={snapshot ? formatMetric(snapshot.ehb, 1) : "Unavailable"} detail="Latest supported snapshot" tone={snapshot ? "info" : "neutral"} icon="activity" />
        <Metric label="EHP" value={snapshot ? formatMetric(snapshot.ehp, 1) : "Unavailable"} detail="Latest supported snapshot" tone={snapshot ? "info" : "neutral"} icon="activity" />
        <Metric label="Collections" value={snapshot ? formatMetric(snapshot.collections) : "Unavailable"} detail="Latest supported snapshot" tone={snapshot ? "info" : "neutral"} icon="checklist" />
        <Metric label="Pets" value={snapshot ? formatMetric(snapshot.petCount) : "Unavailable"} detail="Latest supported snapshot" tone={snapshot ? "info" : "neutral"} icon="badge" />
      </section>

      <div className="profile-grid">
        <Panel title="Open work" eyebrow={`${profile.openCases.length} persisted case links`} icon="review">
          {profile.openCases.length ? <div className="profile-case-list">{profile.openCases.map((item) => <ConsoleLink key={item.caseId} href={`/reviews/${encodeURIComponent(item.caseId)}`} onNavigate={onNavigate} className="profile-case-row"><State value={item.state} /><span><strong>{item.summary}</strong><small><TimeStamp value={item.openedAt} /></small></span><IconGlyph name="chevron-right" size={16} /></ConsoleLink>)}</div> : <EmptyState title="No open work" detail="No active review projection is linked to this player." icon="check" />}
        </Panel>

        <Panel title="Trend history" eyebrow="Snapshots only · default 90d" icon="activity">
          <div className="range-tabs" role="tablist" aria-label="Trend range"><button type="button" className={range === "30d" ? "range-tab active" : "range-tab"} role="tab" aria-selected={range === "30d"} onClick={() => setRange("30d")}>30d</button><button type="button" className={range === "90d" ? "range-tab active" : "range-tab"} role="tab" aria-selected={range === "90d"} onClick={() => setRange("90d")}>90d</button><button type="button" className={range === "all" ? "range-tab active" : "range-tab"} role="tab" aria-selected={range === "all"} onClick={() => setRange("all")}>All</button></div>
          {profile.trend.length ? <div className="trend-table-wrap"><table className="trend-table"><caption className="sr-only">Player snapshot trend for {range}</caption><thead><tr><th scope="col">Observed</th><th scope="col">Total</th><th scope="col">EHB</th><th scope="col">EHP</th><th scope="col">Collections</th><th scope="col">Pets</th></tr></thead><tbody>{profile.trend.map((point) => <tr key={point.timestamp}><td><TimeStamp value={point.timestamp} /></td><td>{formatMetric(point.totalLevel)}</td><td>{formatMetric(point.ehb, 1)}</td><td>{formatMetric(point.ehp, 1)}</td><td>{formatMetric(point.collections)}</td><td>{formatMetric(point.petCount)}</td></tr>)}</tbody></table></div> : <EmptyState title="No snapshots in this range" detail="The API has no persisted PlayerSnapshot rows for this range." icon="history" />}
        </Panel>

        <Panel title="Lifecycle history" eyebrow="Persisted events anchored to this PlayerId" icon="history" className="profile-events">
          {profile.lifecycleEvents.length ? <div className="event-list">{profile.lifecycleEvents.map((event) => <div className="event-row" key={event.eventId}><div className="event-row-icon"><IconGlyph name={event.eventType.includes("MISMATCH") ? "rank-mismatch" : event.eventType.includes("PROMOTION") ? "badge" : "activity"} size={15} /></div><div><strong>{event.label}</strong><small><TimeStamp value={event.createdAt} /> · {event.source ?? "Source unavailable"}</small>{event.evidence?.find((item) => item.label === "Observation")?.value ? <span>{event.evidence.find((item) => item.label === "Observation").value}</span> : null}</div><State value={event.status} /></div>)}</div> : <EmptyState title="No lifecycle events" detail="No non-status events are recorded for this player." icon="history" />}
        </Panel>
      </div>
    </PageFrame>
  );
}
