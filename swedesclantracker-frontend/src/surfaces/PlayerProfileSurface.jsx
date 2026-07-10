import { useMemo } from "react";
import { BeveledButton } from "../components/osrs/BeveledButton";
import { DataTable } from "../components/osrs/DataTable";
import { EmptyFeatureState } from "../components/osrs/EmptyFeatureState";
import { StatusPill } from "../components/osrs/StatusPill";
import { StonePanel } from "../components/osrs/StonePanel";
import { mapPlayerProfileToViewModel } from "../data/viewModels/playerProfileViewModel";

export function PlayerProfileSurface({ player, loading, error, onRetry, onBackToMembers }) {
  const profile = useMemo(() => mapPlayerProfileToViewModel(player), [player]);

  if (loading) return <ProfileState title="Player profile" message="Loading profile…" loading />;
  if (error) return <ProfileState title="Player profile" message={error} tone="danger" action={<BeveledButton variant="ghost" onClick={onRetry}>Retry</BeveledButton>} />;
  if (!profile) return <ProfileState title="Player profile" message="Choose a member from the roster to view their profile." action={<BeveledButton variant="ghost" onClick={onBackToMembers}>Back to members</BeveledButton>} />;

  return (
    <div className="page profile-page">
      <header className="profile-heading">
        <div>
          <div className="profile-name-row"><h1>{profile.username}</h1><StatusPill tone={profile.statusTone}>{profile.statusLabel}</StatusPill></div>
          <p>{profile.currentRank}{profile.eligibleRank ? ` · Eligible for ${profile.eligibleRank}` : ""}</p>
        </div>
        <BeveledButton variant="ghost" icon="members" onClick={onBackToMembers}>Back to members</BeveledButton>
      </header>

      <section className="profile-facts" aria-label="Player state">
        <Fact label="Current rank" value={profile.currentRank} />
        {profile.eligibleRank ? <Fact label="Eligible rank" value={profile.eligibleRank} /> : null}
        <Fact label="Last seen" value={profile.lastSeen.full} />
        <Fact label="Last synced" value={profile.lastSync.full} />
      </section>

      <div className="profile-grid">
        <StonePanel title="Open cases" icon="review">
          {profile.openCases.length ? <ul className="case-list">{profile.openCases.map((item) => <li key={item.id}><span>{item.label}</span><StatusPill tone={item.tone}>{item.type}</StatusPill></li>)}</ul> : <EmptyFeatureState title="No open cases" message="This player has no current review or promotion cases." />}
        </StonePanel>
        <StonePanel title="Recent events" icon="activity">
          <DataTable columns={[{ key: "event", header: "Event", render: (row) => <div className="event-cell"><strong>{row.title}</strong><span>{row.occurredAt.full}</span></div> }, { key: "age", header: "Age", render: (row) => row.timeAgo }]} rows={profile.recentEvents} emptyTitle="No recent events" emptyMessage="No player lifecycle events were returned." />
        </StonePanel>
      </div>
    </div>
  );
}

function Fact({ label, value }) {
  return <div><span>{label}</span><strong>{value}</strong></div>;
}

function ProfileState({ title, message, tone = "info", loading = false, action = null }) {
  return <div className="page surface-state"><header className="page-header"><h1>{title}</h1></header><StonePanel tone={tone}><StatusPill tone={tone} loading={loading}>{message}</StatusPill>{action ? <div className="panel-action-row">{action}</div> : null}</StonePanel></div>;
}
