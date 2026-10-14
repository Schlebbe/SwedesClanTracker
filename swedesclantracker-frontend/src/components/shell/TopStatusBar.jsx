import { StatusPill } from "../osrs/StatusPill";
import { formatRelative, humanize, stateTone } from "../../data/viewModels/consoleViewModels";
import { IconGlyph } from "../osrs/IconGlyph";

export function TopStatusBar({ session, health, onLogout }) {
  const worker = health?.worker;
  const overall = health?.overall ?? "unknown";
  const detail = worker?.observedAt ? `Worker heartbeat ${formatRelative(worker.observedAt)}` : "No worker heartbeat";

  return (
    <header className="osrs-topbar">
      <div className="osrs-topbar-status">
        <span className="eyebrow">WATCHTOWER BRIEF</span>
        <StatusPill tone={stateTone(overall)}>{humanize(overall)}</StatusPill>
        <span>{detail}</span>
      </div>
      <div className="osrs-topbar-session">
        <span className="session-name"><IconGlyph name="profile" size={16} />{session?.username ?? "Operator"}</span>
        <button type="button" className="text-button" onClick={onLogout}>
          <IconGlyph name="logout" size={15} /> Log out
        </button>
      </div>
    </header>
  );
}
