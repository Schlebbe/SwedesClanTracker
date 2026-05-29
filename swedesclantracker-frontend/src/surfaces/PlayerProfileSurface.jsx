import { toneClass } from "../ui";

export function PlayerProfileSurface({ player, loading, error, onRetry, onBackToMembers }) {
  if (loading) {
    return (
      <div className="surface-grid">
      <header className="surface-header">
        <p className="eyebrow">Player Profile</p>
        <h2>Loading Player Profile...</h2>
      </header>
      </div>
    );
  }

  if (error) {
    return (
      <div className="surface-grid">
        <header className="surface-header">
          <p className="eyebrow">Player Profile</p>
          <h2>Unable to Load Profile</h2>
        </header>
        <section className="panel">
          <p className="tone tone-danger">{error}</p>
          <div className="message-action">
            <button className="btn-ghost" onClick={onRetry}>Retry</button>
            <button className="btn-ghost" onClick={onBackToMembers}>Back to Members</button>
          </div>
        </section>
      </div>
    );
  }

  if (!player) {
    return (
      <div className="surface-grid">
        <header className="surface-header">
          <p className="eyebrow">Player Profile</p>
          <h2>No Player Selected</h2>
        </header>
        <section className="panel">
          <p className="empty-note">Choose a member from Members to load a profile.</p>
          <div className="message-action"><button className="btn-ghost" onClick={onBackToMembers}>Back to Members</button></div>
        </section>
      </div>
    );
  }

  const rankHistoryAvailable = Boolean(player.historyAvailability?.rankHistoryAvailable);
  const statHistoryAvailable = Boolean(player.historyAvailability?.statHistoryAvailable);
  const historyReason = player.historyAvailability?.reason ?? "History endpoints are not available yet.";

  return (
    <div className="surface-grid">
      <header className="surface-header">
        <p className="eyebrow">Player Profile</p>
        <h2 title={player.username}>{player.username}</h2>
        <p>Player state, recent lifecycle events, and open operational cases.</p>
      </header>

      <section className="layout-two">
        <article className="panel">
          <h3>Current State</h3>
          <ul className="stack-list compact">
            <li className="line-item"><span>Current rank</span><strong>{player.currentRank ?? "unknown"}</strong></li>
            <li className="line-item"><span>Eligible rank</span><strong>{player.eligibleRank ?? "unknown"}</strong></li>
            <li className="line-item"><span>Status</span><strong>{player.status ?? "unknown"}</strong></li>
            <li className="line-item"><span>Last sync</span><strong>{player.lastSync ? new Date(player.lastSync).toLocaleString("sv-SE") : "unknown"}</strong></li>
            <li className="line-item"><span>Last seen</span><strong>{player.lastSeen ? new Date(player.lastSeen).toLocaleString("sv-SE") : "unknown"}</strong></li>
          </ul>
        </article>

        <article className="panel">
          <h3>Open Cases</h3>
          <ul className="stack-list compact">
            {player.openCases?.length ? player.openCases.map((item) => (
              <li key={`${item.type}-${item.label}`} className="line-item">
                <span>{item.label}</span>
                <span className={toneClass(item.type === "mismatch" ? "danger" : item.type === "promotion" ? "success" : "warning")}>{item.type}</span>
              </li>
            )) : <li className="line-item"><span>No open cases.</span></li>}
          </ul>
        </article>
      </section>

      <section className="panel">
        <h3>Recent Player Events</h3>
        <ul className="stack-list compact">
          {player.recentEvents?.length ? player.recentEvents.map((item) => (
            <li key={item.id} className="line-item">
              <div>
                <strong>{item.title}</strong>
                <p>{new Date(item.occurredAt).toLocaleString("sv-SE")}</p>
              </div>
              <span className={toneClass("info")}>{item.timeAgo}</span>
            </li>
          )) : <li className="line-item"><span>No recent events for this player.</span></li>}
        </ul>
      </section>

      <section className="layout-two">
        {rankHistoryAvailable ? (
          <article className="panel">
            <strong>Rank history</strong>
            <p>Rank history data is available and will be rendered here in a future slice.</p>
          </article>
        ) : (
          <article className="panel placeholder">
            <strong>Rank history area</strong>
            <p>{historyReason}</p>
          </article>
        )}
        {statHistoryAvailable ? (
          <article className="panel">
            <strong>Stat history</strong>
            <p>Stat history data is available and will be rendered here in a future slice.</p>
          </article>
        ) : (
          <article className="panel placeholder">
            <strong>Stat history area</strong>
            <p>{historyReason}</p>
          </article>
        )}
      </section>
    </div>
  );
}
