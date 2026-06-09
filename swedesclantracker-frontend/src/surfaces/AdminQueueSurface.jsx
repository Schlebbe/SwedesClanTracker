import { useMemo } from "react";
import { BeveledButton } from "../components/osrs/BeveledButton";
import { EmptyFeatureState } from "../components/osrs/EmptyFeatureState";
import { StatusPill } from "../components/osrs/StatusPill";
import { StonePanel } from "../components/osrs/StonePanel";
import { UnavailableMetric } from "../components/osrs/UnavailableMetric";
import { mapAdminQueueToReviewQueueViewModel } from "../data/viewModels/reviewQueueViewModel";

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
  const queue = useMemo(
    () => mapAdminQueueToReviewQueueViewModel(cases, selectedCase, selectedCaseId),
    [cases, selectedCase, selectedCaseId]
  );

  return (
    <div className="surface-grid review-queues-surface">
      <header className="surface-header review-header">
        <div>
          <p className="eyebrow">Review Queues</p>
          <h2>Officer triage for member identity and rank decisions</h2>
          <p>Grouped review cases using the app queue data that already exists.</p>
        </div>
        <StatusPill tone={queue.totalCount ? "warning" : "success"}>
          {queue.totalCount} open
        </StatusPill>
      </header>

      {loading ? (
        <StonePanel>
          <StatusPill tone="info" loading>Loading review queues...</StatusPill>
        </StonePanel>
      ) : null}

      {error ? (
        <StonePanel tone="danger">
          <EmptyFeatureState
            title="Unable to load review queues"
            message={error}
            tone="danger"
            action={<BeveledButton onClick={onRetryList}>Retry</BeveledButton>}
          />
        </StonePanel>
      ) : null}

      {!loading && !error ? (
        <section className="review-layout">
          <div className="review-group-stack">
            {queue.totalCount ? null : (
              <StonePanel>
                <EmptyFeatureState
                  title="No open review cases"
                  message="There are no rename, missing-member, or rank-review cases in the current admin queue."
                />
              </StonePanel>
            )}

            {queue.groups.map((group) => (
              <StonePanel
                key={group.id}
                title={group.label}
                subtitle={group.description}
                actions={<StatusPill tone={group.items.length ? "warning" : "success"}>{group.items.length}</StatusPill>}
                className="review-group-panel"
              >
                {group.items.length ? (
                  <ul className="review-card-grid">
                    {group.items.map((item) => (
                      <li key={item.id}>
                        <button
                          type="button"
                          className={selectedCaseId === item.caseId ? "review-card review-card-active" : "review-card"}
                          disabled={!item.canOpenDetail}
                          onClick={() => {
                            if (item.canOpenDetail) onSelectCase(item.caseId);
                          }}
                        >
                          <span className="review-card-head">
                            <small>{item.type}</small>
                            <StatusPill tone={item.riskTone}>{item.risk}</StatusPill>
                          </span>
                          <strong title={item.title}>{item.title}</strong>
                          <span className="review-player" title={item.player}>{item.player}</span>
                          <span className="review-card-meta">
                            {item.confidenceLabel ? <StatusPill tone="info">confidence: {item.confidenceLabel}</StatusPill> : null}
                            <span>{item.age}</span>
                          </span>
                          {item.recommendedAction ? <em>{item.recommendedAction}</em> : null}
                        </button>
                      </li>
                    ))}
                  </ul>
                ) : (
                  <EmptyFeatureState
                    title="Queue clear"
                    message={`No ${group.label.toLowerCase()} are currently exposed by the admin queue.`}
                  />
                )}
              </StonePanel>
            ))}
          </div>

          <StonePanel title="Case Detail" subtitle="Evidence and decision notes" className="review-detail-panel">
            {detailLoading ? <StatusPill tone="info" loading>Loading case detail...</StatusPill> : null}

            {detailError ? (
              <EmptyFeatureState
                title="Unable to load case detail"
                message={detailError}
                tone="danger"
                action={<BeveledButton onClick={onRetryDetail}>Retry detail</BeveledButton>}
              />
            ) : null}

            {!detailLoading && !detailError && queue.selectedCase ? (
              <ReviewCaseDetail detail={queue.selectedCase} />
            ) : null}

            {!detailLoading && !detailError && !queue.selectedCase ? (
              <EmptyFeatureState
                title="No case selected"
                message="Choose a review card to inspect the available evidence and recommendations."
              />
            ) : null}
          </StonePanel>
        </section>
      ) : null}
    </div>
  );
}

function ReviewCaseDetail({ detail }) {
  return (
    <div className="review-detail">
      <div className="review-detail-title">
        <div>
          <p className="eyebrow">{detail.type}</p>
          <h3 title={detail.title}>{detail.title}</h3>
          <p title={detail.player}>{detail.player}</p>
        </div>
        <StatusPill tone={detail.riskTone}>{detail.risk}</StatusPill>
      </div>

      <dl className="review-detail-grid">
        <div>
          <dt>Age</dt>
          <dd>{detail.age}</dd>
        </div>
        {detail.confidenceLabel ? (
          <div>
            <dt>Confidence</dt>
            <dd>{detail.confidenceLabel}</dd>
          </div>
        ) : null}
        {detail.recommendedAction ? (
          <div className="review-detail-wide">
            <dt>Recommended Action</dt>
            <dd>{detail.recommendedAction}</dd>
          </div>
        ) : null}
      </dl>

      <ReviewDetailList title="Evidence" items={detail.evidence} emptyMessage="No evidence is currently provided for this case." />
      <ReviewDetailList title="Alternatives" items={detail.alternatives} emptyMessage="No alternatives are currently provided for this case." />

      {detail.dangerousNote ? (
        <div className="review-danger-note">
          <strong>Dangerous Action Note</strong>
          <p>{detail.dangerousNote}</p>
        </div>
      ) : null}

      <div className="review-detail-actions">
        <UnavailableMetric
          label="Direct case actions unavailable"
          reason="The app queue currently exposes guidance only, not executable action contracts."
        />
      </div>
    </div>
  );
}

function ReviewDetailList({ title, items, emptyMessage }) {
  return (
    <section className="review-detail-section">
      <h4>{title}</h4>
      {items.length ? (
        <ul className="review-detail-list">
          {items.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>
      ) : (
        <EmptyFeatureState title={`No ${title.toLowerCase()}`} message={emptyMessage} />
      )}
    </section>
  );
}
