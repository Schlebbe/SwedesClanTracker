import { toneClass } from "../ui";

const laneOrder = ["safe", "inspect", "high-risk"];

export function AdminQueueSurface({
  cases,
  selectedCase,
  selectedCaseId,
  loading,
  error,
  detailLoading,
  detailError,
  onRetryList,
  onRetryDetail,
  onSelectCase,
}) {
  return (
    <div className="surface-grid">
      <header className="surface-header">
        <p className="eyebrow">Admin Queue</p>
        <h2>Unified Execution Workflow for Promotions and Reviews</h2>
        <p>Case-first triage with evidence and action separation, not stacked raw tables.</p>
      </header>

      {loading ? (
        <section className="panel"><p className="tone tone-info" data-loading="true">Loading admin queue...</p></section>
      ) : null}

      {error ? (
        <section className="panel">
          <p className="tone tone-danger">Unable to load admin queue: {error}</p>
          <div className="message-action"><button className="btn-ghost" onClick={onRetryList}>Retry</button></div>
        </section>
      ) : null}

      {!loading && !error ? (
        <section className="queue-layout">
          <div className="queue-columns">
            {laneOrder.map((lane) => {
              const items = cases.filter((item) => item.lane === lane);
              return (
                <article key={lane} className="panel queue-lane">
                  <h3>{lane}</h3>
                  {items.length ? (
                    <ul className="queue-list">
                      {items.map((item) => (
                        <li key={item.id}>
                          <button className={selectedCaseId === item.id ? "case-card case-card-active" : "case-card"} onClick={() => onSelectCase(item.id)}>
                            <small>{item.type}</small>
                            <strong title={item.title}>{item.title}</strong>
                            <p title={item.player}>{item.player}</p>
                            <div className="case-meta">
                              <span className={toneClass(item.risk === "high" ? "danger" : item.risk === "medium" ? "warning" : "success")}>{item.risk}</span>
                              <span>{item.age ?? "unknown"}</span>
                            </div>
                          </button>
                        </li>
                      ))}
                    </ul>
                  ) : <p className="empty-note">No cases in this lane.</p>}
                </article>
              );
            })}
          </div>

          <aside className="panel case-detail">
            {detailLoading ? <p className="tone tone-info" data-loading="true">Loading case detail...</p> : null}
            {detailError ? (
              <>
                <p className="tone tone-danger">Unable to load case detail: {detailError}</p>
                <div className="message-action"><button className="btn-ghost" onClick={onRetryDetail}>Retry detail</button></div>
              </>
            ) : null}

            {!detailLoading && !detailError && selectedCase ? (
              <>
                <h3 title={selectedCase.title}>{selectedCase.title}</h3>
                <p className="case-player" title={selectedCase.player}>{selectedCase.player} | {selectedCase.type}</p>
                <p className="recommend">Recommended: {selectedCase.recommendedAction}</p>

                <h4>Evidence</h4>
                <ul className="stack-list compact">
                  {selectedCase.evidence.length ? selectedCase.evidence.map((item) => <li key={item} className="line-item"><span>{item}</span></li>) : <li className="line-item"><span>No evidence available.</span></li>}
                </ul>

                <h4>Alternative actions</h4>
                <ul className="stack-list compact">
                  {selectedCase.alternatives.length ? selectedCase.alternatives.map((item) => <li key={item} className="line-item"><span>{item}</span></li>) : <li className="line-item"><span>No alternatives available.</span></li>}
                </ul>

                <div className="danger-zone">
                  <p>Dangerous action zone</p>
                  <small>{selectedCase.dangerous ?? selectedCase.danger}</small>
                </div>
              </>
            ) : null}

            {!detailLoading && !detailError && !selectedCase ? <p className="empty-note">No case selected. Choose a lane item when available.</p> : null}
          </aside>
        </section>
      ) : null}
    </div>
  );
}
