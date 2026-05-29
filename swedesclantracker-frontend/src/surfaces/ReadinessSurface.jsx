import { toneClass } from "../ui";

export function ReadinessSurface({ readiness, loading, error, onRetry }) {
  const runtime = Array.isArray(readiness?.runtime) ? readiness.runtime : [];
  const config = Array.isArray(readiness?.config) ? readiness.config : [];

  return (
    <div className="surface-grid">
      <header className="surface-header">
        <p className="eyebrow">Readiness</p>
        <h2>Runtime, Integration, and Config Health</h2>
      </header>

      {loading ? <section className="panel"><p className="tone tone-info" data-loading="true">Loading readiness status...</p></section> : null}

      {error ? (
        <section className="panel">
          <p className="tone tone-danger">Unable to load readiness status: {error}</p>
          <div className="message-action"><button className="btn-ghost" onClick={onRetry}>Retry</button></div>
        </section>
      ) : null}

      {!loading && !error ? (
        <section className="layout-two">
          <article className="panel">
            <h3>Runtime Status</h3>
            <ul className="stack-list">
              {runtime.length ? runtime.map((item) => (
                <li key={item.label} className="line-item">
                  <div>
                    <strong>{item.label}</strong>
                    <p>{item.detail}</p>
                  </div>
                  <span className={toneClass(item.tone)}>{item.state}</span>
                </li>
              )) : <li className="line-item"><span>No runtime status available yet.</span></li>}
            </ul>
          </article>

          <article className="panel">
            <h3>Config Readiness</h3>
            <ul className="stack-list compact">
              {config.length ? config.map((item) => (
                <li key={item.key} className="line-item"><span>{item.key}</span><strong>{item.value}</strong></li>
              )) : <li className="line-item"><span>No config readiness signals available yet.</span></li>}
            </ul>
          </article>
        </section>
      ) : null}
    </div>
  );
}
