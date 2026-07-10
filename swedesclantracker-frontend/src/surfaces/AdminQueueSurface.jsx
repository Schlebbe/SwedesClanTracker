import { useMemo } from "react";
import { BeveledButton } from "../components/osrs/BeveledButton";
import { EmptyFeatureState } from "../components/osrs/EmptyFeatureState";
import { StatusPill } from "../components/osrs/StatusPill";
import { StonePanel } from "../components/osrs/StonePanel";
import { mapAdminQueueToReviewQueueViewModel } from "../data/viewModels/reviewQueueViewModel";

export function AdminQueueSurface({ cases, selectedCase, selectedCaseId, loading, error, detailLoading, detailError, onRetryList, onRetryDetail, onSelectCase }) {
  const queue = useMemo(() => mapAdminQueueToReviewQueueViewModel(cases, selectedCase, selectedCaseId), [cases, selectedCase, selectedCaseId]);

  return (
    <div className="page review-page">
      <header className="page-header">
        <div>
          <h1>Review queues</h1>
          <p>Cases that need a decision about identity, membership, or rank.</p>
        </div>
        <StatusPill tone={queue.totalCount ? "warning" : "success"}>{queue.totalCount.toLocaleString()} open</StatusPill>
      </header>

      {loading ? <StonePanel><StatusPill tone="info" loading>Loading review queues…</StatusPill></StonePanel> : null}
      {error ? <StonePanel tone="danger"><EmptyFeatureState title="Review queues could not be loaded" message={error} tone="danger" action={<BeveledButton variant="ghost" onClick={onRetryList}>Retry</BeveledButton>} /></StonePanel> : null}

      {!loading && !error ? (
        <div className="review-layout">
          <div className="review-group-stack">
            {queue.groups.map((group) => (
              <StonePanel key={group.id} title={group.label} icon={groupIcon(group.id)} actions={<StatusPill tone={group.items.length ? "warning" : "neutral"}>{group.items.length}</StatusPill>}>
                {group.items.length ? (
                  <ul className="review-card-list">
                    {group.items.map((item) => (
                      <li key={item.id}>
                        <button type="button" className={selectedCaseId === item.caseId ? "review-card review-card-active" : "review-card"} onClick={() => item.canOpenDetail && onSelectCase(item.caseId)}>
                          <span className="review-card-top"><strong>{item.title}</strong><StatusPill tone={item.riskTone}>{item.risk}</StatusPill></span>
                          <span>{item.player}</span>
                          <small>{item.type} · {item.age}</small>
                          {item.recommendedAction ? <small>{item.recommendedAction}</small> : null}
                        </button>
                      </li>
                    ))}
                  </ul>
                ) : <EmptyFeatureState title="Queue clear" message={`No ${group.label.toLowerCase()} are open.`} />}
              </StonePanel>
            ))}
          </div>

          <StonePanel title="Case details" icon="scroll" className="review-detail-panel">
            {detailLoading ? <StatusPill tone="info" loading>Loading case…</StatusPill> : null}
            {detailError ? <EmptyFeatureState title="Case details unavailable" message={detailError} tone="danger" action={<BeveledButton variant="ghost" onClick={onRetryDetail}>Retry</BeveledButton>} /> : null}
            {!detailLoading && !detailError && queue.selectedCase ? <ReviewCaseDetail detail={queue.selectedCase} /> : null}
            {!detailLoading && !detailError && !queue.selectedCase ? <EmptyFeatureState title="Select a case" message="Choose an open case to see the available evidence." /> : null}
          </StonePanel>
        </div>
      ) : null}
    </div>
  );
}

function groupIcon(id) {
  if (id === "rsn-changes") return "name-change";
  if (id === "missing-members") return "member-alert";
  if (id === "rank-reviews") return "promotion";
  return "review";
}

function ReviewCaseDetail({ detail }) {
  return (
    <div className="review-detail">
      <div className="review-detail-heading"><div><span className="detail-type">{detail.type}</span><h2>{detail.title}</h2><p>{detail.player}</p></div><StatusPill tone={detail.riskTone}>{detail.risk}</StatusPill></div>
      <dl className="review-facts">
        <div><dt>Age</dt><dd>{detail.age}</dd></div>
        {detail.confidenceLabel ? <div><dt>Confidence</dt><dd>{detail.confidenceLabel}</dd></div> : null}
        {detail.recommendedAction ? <div className="review-fact-wide"><dt>Recommended action</dt><dd>{detail.recommendedAction}</dd></div> : null}
      </dl>
      <DetailList title="Evidence" items={detail.evidence} emptyMessage="No evidence was returned for this case." />
      <DetailList title="Other options" items={detail.alternatives} emptyMessage="No other options were returned for this case." />
      {detail.dangerousNote ? <div className="review-warning"><strong>Before you act</strong><p>{detail.dangerousNote}</p></div> : null}
    </div>
  );
}

function DetailList({ title, items, emptyMessage }) {
  return <section className="detail-list"><h3>{title}</h3>{items.length ? <ul>{items.map((item) => <li key={item}>{item}</li>)}</ul> : <p>{emptyMessage}</p>}</section>;
}
