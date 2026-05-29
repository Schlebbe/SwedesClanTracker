import { useState } from "react";

export function ClanLogSurface({ log, loading, error, onRetry }) {
  const [filter, setFilter] = useState("important");

  const map = {
    promotions: "Promotion",
    roster: "Roster",
    reviews: "Review",
    "sync-system": "System",
  };

  const filters = Array.isArray(log?.filters) ? log.filters : ["important", "promotions", "roster", "reviews", "sync-system", "all"];
  const important = Array.isArray(log?.important) ? log.important : [];
  const routine = Array.isArray(log?.routine) ? log.routine : [];

  const visibleImportant = filter === "all" || filter === "important"
    ? important
    : important.filter((item) => item.group === map[filter]);

  const showRoutine = filter === "all" || filter === "important" || filter === "sync-system";
  const toneByGroup = {
    Promotion: "success",
    Review: "warning",
    Roster: "info",
    System: "info",
  };

  return (
    <div className="surface-grid">
      <header className="surface-header">
        <p className="eyebrow">Clan Log</p>
        <h2>Meaningful History First, Routine Sync Noise Reduced</h2>
      </header>

      {loading ? <section className="panel"><p className="tone tone-info" data-loading="true">Loading clan log...</p></section> : null}

      {error ? (
        <section className="panel">
          <p className="tone tone-danger">Unable to load clan log: {error}</p>
          <div className="message-action"><button className="btn-ghost" onClick={onRetry}>Retry</button></div>
        </section>
      ) : null}

      {!loading && !error ? (
        <>
          <section className="panel filter-row" aria-label="Clan log filters">
            {filters.map((item) => (
              <button key={item} className={filter === item ? "chip chip-active" : "chip"} onClick={() => setFilter(item)}>{item}</button>
            ))}
          </section>

          <section className="layout-two">
            <article className="panel">
              <h3>Important Events</h3>
              <ul className="stack-list">
                {visibleImportant.length ? visibleImportant.map((item) => (
                  <li key={item.id} className="line-item log-item">
                    <div className="log-main">
                      <div className="log-heading">
                        <span className={toneByGroup[item.group] ? `tone tone-${toneByGroup[item.group]}` : "tone tone-info"}>{item.group}</span>
                        <strong>{item.title}</strong>
                      </div>
                      <p className="log-detail">{item.detail}</p>
                    </div>
                    <span className="log-time">{item.time ?? "unknown"}</span>
                  </li>
                )) : <li className="line-item"><span>No important events match this filter.</span></li>}
              </ul>
            </article>

            <article className="panel">
              <h3>Routine Sync/System Bundle</h3>
              <ul className="stack-list compact">
                {showRoutine ? (routine.length ? routine.map((item) => <li key={item} className="line-item"><span>{item}</span></li>) : <li className="line-item"><span>No routine sync entries available.</span></li>) : <li className="line-item"><span>Routine bundle hidden for this filter.</span></li>}
              </ul>
            </article>
          </section>
        </>
      ) : null}
    </div>
  );
}
