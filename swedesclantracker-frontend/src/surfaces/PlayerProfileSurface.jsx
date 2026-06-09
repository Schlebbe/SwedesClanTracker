import { useMemo } from "react";
import { BeveledButton } from "../components/osrs/BeveledButton";
import { DataTable } from "../components/osrs/DataTable";
import { EmptyFeatureState } from "../components/osrs/EmptyFeatureState";
import { IconGlyph } from "../components/osrs/IconGlyph";
import { StatCard } from "../components/osrs/StatCard";
import { StatusPill } from "../components/osrs/StatusPill";
import { StonePanel } from "../components/osrs/StonePanel";
import { UnavailableMetric } from "../components/osrs/UnavailableMetric";
import { mapPlayerProfileToViewModel } from "../data/viewModels/playerProfileViewModel";

export function PlayerProfileSurface({ player, loading, error, onRetry, onBackToMembers }) {
  const profile = useMemo(() => mapPlayerProfileToViewModel(player), [player]);

  if (loading) {
    return (
      <div className="surface-grid profile-surface">
        <header className="surface-header">
          <p className="eyebrow">Player Profile</p>
          <h2>Loading player profile</h2>
        </header>
        <StonePanel>
          <StatusPill tone="info" loading>Loading profile...</StatusPill>
        </StonePanel>
      </div>
    );
  }

  if (error) {
    return (
      <div className="surface-grid profile-surface">
        <header className="surface-header">
          <p className="eyebrow">Player Profile</p>
          <h2>Unable to load profile</h2>
        </header>
        <StonePanel tone="danger">
          <EmptyFeatureState
            title="Profile request failed"
            message={error}
            tone="danger"
            action={(
              <div className="profile-action-row">
                <BeveledButton onClick={onRetry}>Retry</BeveledButton>
                <BeveledButton variant="ghost" onClick={onBackToMembers}>Back to Members</BeveledButton>
              </div>
            )}
          />
        </StonePanel>
      </div>
    );
  }

  if (!profile) {
    return (
      <div className="surface-grid profile-surface">
        <header className="surface-header">
          <p className="eyebrow">Player Profile</p>
          <h2>No player selected</h2>
        </header>
        <StonePanel>
          <EmptyFeatureState
            title="Choose a clan member"
            message="Open a profile from the Clan Members roster to inspect player state and recent events."
            action={<BeveledButton variant="ghost" onClick={onBackToMembers}>Back to Members</BeveledButton>}
          />
        </StonePanel>
      </div>
    );
  }

  return (
    <div className="surface-grid profile-surface">
      <header className="profile-hero">
        <div className="profile-avatar" aria-hidden="true">
          <IconGlyph name="profile" className="profile-avatar-icon" />
        </div>
        <div className="profile-title-block">
          <p className="eyebrow">Player Profile</p>
          <h2 title={profile.username}>{profile.username}</h2>
          <div className="profile-status-line">
            <StatusPill tone={profile.statusTone}>{profile.statusLabel}</StatusPill>
            <span>{profile.currentRank}</span>
            {profile.eligibleRank ? <span>Eligible: {profile.eligibleRank}</span> : null}
          </div>
        </div>
        <BeveledButton variant="ghost" icon="members" onClick={onBackToMembers}>Back to Members</BeveledButton>
      </header>

      <section className="profile-stat-grid">
        {profile.summaryCards.map((card) => (
          <StatCard
            key={card.key}
            label={card.label}
            value={card.value}
            detail={card.detail}
            tone={card.tone}
            available={card.available}
            unavailableReason={card.unavailableReason}
          />
        ))}
      </section>

      <section className="profile-main-grid">
        <div className="profile-main-stack">
          <StonePanel title="Current State" icon="profile">
            <dl className="profile-fact-grid">
              <div>
                <dt>Username</dt>
                <dd title={profile.username}>{profile.username}</dd>
              </div>
              <div>
                <dt>Current Rank</dt>
                <dd>{profile.currentRank}</dd>
              </div>
              {profile.eligibleRank ? (
                <div>
                  <dt>Eligible Rank</dt>
                  <dd>{profile.eligibleRank}</dd>
                </div>
              ) : null}
              <div>
                <dt>Lifecycle Status</dt>
                <dd><StatusPill tone={profile.statusTone}>{profile.statusLabel}</StatusPill></dd>
              </div>
              <div>
                <dt>Last Seen</dt>
                <dd>{profile.lastSeen.full}</dd>
              </div>
              <div>
                <dt>Last Synced</dt>
                <dd>{profile.lastSync.full}</dd>
              </div>
            </dl>
          </StonePanel>

          <StonePanel title="Latest Snapshot" icon="scroll" variant="table">
            {profile.latestSnapshot.available ? (
              <DataTable
                columns={[
                  { key: "label", header: "Metric" },
                  { key: "value", header: "Value" },
                ]}
                rows={profile.latestSnapshot.rows}
                emptyTitle="No snapshot values"
                emptyMessage={profile.latestSnapshot.reason}
              />
            ) : (
              <UnavailableMetric
                label="Latest snapshot values unavailable"
                reason={profile.latestSnapshot.reason}
              />
            )}
          </StonePanel>

          <StonePanel title="Recent Events" icon="activity" variant="table">
            <DataTable
              columns={[
                {
                  key: "event",
                  header: "Event",
                  render: (row) => (
                    <div className="profile-event-cell">
                      <strong>{row.title}</strong>
                      <span>{row.occurredAt.full}</span>
                    </div>
                  ),
                },
                {
                  key: "timeAgo",
                  header: "Age",
                  render: (row) => row.timeAgo ? <StatusPill tone={row.tone}>{row.timeAgo}</StatusPill> : "Unknown",
                },
              ]}
              rows={profile.recentEvents}
              emptyTitle="No recent events"
              emptyMessage="This player does not have recent lifecycle events exposed by the profile endpoint."
            />
          </StonePanel>
        </div>

        <aside className="profile-side-stack">
          <StonePanel
            title="Open Cases"
            icon="review"
            actions={<StatusPill tone={profile.openCases.length ? "warning" : "success"}>{profile.openCases.length}</StatusPill>}
          >
            {profile.openCases.length ? (
              <ul className="profile-case-list">
                {profile.openCases.map((item) => (
                  <li key={item.id}>
                    <span>{item.label}</span>
                    <StatusPill tone={item.tone}>{item.type}</StatusPill>
                  </li>
                ))}
              </ul>
            ) : (
              <EmptyFeatureState title="No open cases" message="No review, promotion, or rank mismatch cases are currently exposed for this player." />
            )}
          </StonePanel>

          <StonePanel title="Future Profile Modules" icon="future" variant="muted" compact>
            <div className="profile-unavailable-grid">
              {profile.futureSections.map((feature) => (
                <UnavailableMetric key={feature.reason} label={feature.label} reason={feature.reason} />
              ))}
            </div>
          </StonePanel>
        </aside>
      </section>
    </div>
  );
}
