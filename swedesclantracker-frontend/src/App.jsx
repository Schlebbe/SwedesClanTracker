import { useEffect, useState } from "react";
import "./index.css";
import { fetchAdminQueue, fetchAdminQueueCase, fetchClanLog, fetchHome, fetchLiveStatus, fetchPlayerProfile, fetchReadiness, fetchRoster } from "./data/appDataApi";
import { AdminQueueSurface } from "./surfaces/AdminQueueSurface";
import { ClanLogSurface } from "./surfaces/ClanLogSurface";
import { DashboardSurface } from "./surfaces/DashboardSurface";
import { MembersSurface } from "./surfaces/MembersSurface";
import { PlayerProfileSurface } from "./surfaces/PlayerProfileSurface";
import { ReadinessSurface } from "./surfaces/ReadinessSurface";

const surfaces = ["Dashboard", "Members", "Player Profile", "Clan Log", "Admin Queue", "Readiness"];

export default function App() {
  const [surface, setSurface] = useState("Dashboard");

  const [homeState, setHomeState] = useState({ loading: true, error: "", data: null });
  const [queueState, setQueueState] = useState({ loading: true, error: "", cases: [] });
  const [selectedCaseId, setSelectedCaseId] = useState(null);
  const [caseDetailState, setCaseDetailState] = useState({ loading: false, error: "", data: null });

  const [rosterState, setRosterState] = useState({ loading: true, error: "", rows: [] });
  const [selectedPlayerId, setSelectedPlayerId] = useState(null);
  const [profileState, setProfileState] = useState({ loading: false, error: "", data: null });

  const [clanLogState, setClanLogState] = useState({ loading: true, error: "", data: null });
  const [readinessState, setReadinessState] = useState({ loading: true, error: "", data: null });
  const [liveStatusState, setLiveStatusState] = useState({ loading: true, error: "", data: null, stale: false });

  useEffect(() => {
    let active = true;

    async function loadHome() {
      setHomeState((prev) => ({ ...prev, loading: true, error: "" }));
      try {
        const data = await fetchHome();
        if (!active) return;
        setHomeState({ loading: false, error: "", data });
      } catch (error) {
        if (!active) return;
        setHomeState({ loading: false, error: error?.message ?? "Failed loading dashboard.", data: null });
      }
    }

    loadHome();
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    let active = true;

    async function loadQueue() {
      setQueueState((prev) => ({ ...prev, loading: true, error: "" }));
      try {
        const cases = await fetchAdminQueue();
        if (!active) return;
        setQueueState({ loading: false, error: "", cases: Array.isArray(cases) ? cases : [] });
      } catch (error) {
        if (!active) return;
        setQueueState({ loading: false, error: error?.message ?? "Failed loading admin queue.", cases: [] });
      }
    }

    loadQueue();
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    let active = true;

    async function loadRoster() {
      setRosterState((prev) => ({ ...prev, loading: true, error: "" }));
      try {
        const data = await fetchRoster();
        const rows = Array.isArray(data?.rows) ? data.rows : [];
        if (!active) return;
        setRosterState({ loading: false, error: "", rows });
      } catch (error) {
        if (!active) return;
        setRosterState({ loading: false, error: error?.message ?? "Failed loading roster.", rows: [] });
      }
    }

    loadRoster();
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    let active = true;

    async function loadClanLog() {
      setClanLogState((prev) => ({ ...prev, loading: true, error: "" }));
      try {
        const data = await fetchClanLog();
        if (!active) return;
        setClanLogState({ loading: false, error: "", data });
      } catch (error) {
        if (!active) return;
        setClanLogState({ loading: false, error: error?.message ?? "Failed loading clan log.", data: null });
      }
    }

    loadClanLog();
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    let active = true;

    async function loadReadiness() {
      setReadinessState((prev) => ({ ...prev, loading: true, error: "" }));
      try {
        const data = await fetchReadiness();
        if (!active) return;
        setReadinessState({ loading: false, error: "", data });
      } catch (error) {
        if (!active) return;
        setReadinessState({ loading: false, error: error?.message ?? "Failed loading readiness.", data: null });
      }
    }

    loadReadiness();
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    if (surface !== "Dashboard") {
      return undefined;
    }

    let active = true;
    let timerId = null;

    async function tick(isFirst = false) {
      if (isFirst) {
        setLiveStatusState((prev) => ({ ...prev, loading: !prev.data, error: "" }));
      }

      try {
        const data = await fetchLiveStatus();
        if (!active) return;
        setLiveStatusState({ loading: false, error: "", data, stale: false });
      } catch (error) {
        if (!active) return;
        setLiveStatusState((prev) => ({
          ...prev,
          loading: false,
          error: error?.message ?? "Live status update failed.",
          stale: Boolean(prev.data),
        }));
      }

      const hidden = typeof document !== "undefined" && document.visibilityState === "hidden";
      const delayMs = hidden ? 20000 : 3000;
      timerId = setTimeout(() => {
        tick();
      }, delayMs);
    }

    tick(true);

    const onVisibility = () => {
      if (!active) return;
      if (timerId) {
        clearTimeout(timerId);
      }
      tick();
    };

    if (typeof document !== "undefined") {
      document.addEventListener("visibilitychange", onVisibility);
    }

    return () => {
      active = false;
      if (timerId) {
        clearTimeout(timerId);
      }
      if (typeof document !== "undefined") {
        document.removeEventListener("visibilitychange", onVisibility);
      }
    };
  }, [surface]);

  useEffect(() => {
    if (!queueState.cases.length) {
      setSelectedCaseId(null);
      return;
    }

    if (!selectedCaseId || !queueState.cases.some((item) => item.id === selectedCaseId)) {
      setSelectedCaseId(queueState.cases[0].id);
    }
  }, [queueState.cases, selectedCaseId]);

  useEffect(() => {
    if (!selectedCaseId) {
      setCaseDetailState({ loading: false, error: "", data: null });
      return;
    }

    let active = true;

    async function loadCaseDetail() {
      setCaseDetailState((prev) => ({ ...prev, loading: true, error: "" }));
      try {
        const data = await fetchAdminQueueCase(selectedCaseId);
        if (!active) return;
        setCaseDetailState({ loading: false, error: "", data });
      } catch (error) {
        if (!active) return;
        setCaseDetailState({ loading: false, error: error?.message ?? "Failed loading case detail.", data: null });
      }
    }

    loadCaseDetail();
    return () => {
      active = false;
    };
  }, [selectedCaseId]);

  useEffect(() => {
    if (!selectedPlayerId) {
      setProfileState({ loading: false, error: "", data: null });
      return;
    }

    let active = true;

    async function loadProfile() {
      setProfileState((prev) => ({ ...prev, loading: true, error: "" }));
      try {
        const data = await fetchPlayerProfile(selectedPlayerId);
        if (!active) return;
        setProfileState({ loading: false, error: "", data });
      } catch (error) {
        if (!active) return;
        setProfileState({ loading: false, error: error?.message ?? "Failed loading player profile.", data: null });
      }
    }

    loadProfile();
    return () => {
      active = false;
    };
  }, [selectedPlayerId]);

  return (
    <main className="app-shell">
      <aside className="app-nav">
        <div>
          <p className="eyebrow">SwedesClanTracker</p>
          <h1>Clan Tracker Console</h1>
          <p className="nav-meta">Swedes Clan | LAN tracker operations</p>
        </div>

        <nav className="nav-list" aria-label="Primary">
          {surfaces.map((item) => (
            <button key={item} className={surface === item ? "nav-item nav-item-active" : "nav-item"} onClick={() => setSurface(item)}>
              <span>{item}</span>
              {item === "Admin Queue" ? <small>{queueState.cases.length}</small> : null}
            </button>
          ))}
        </nav>
      </aside>

      <section className="app-stage">
        {surface === "Dashboard" ? (
          <DashboardSurface
            data={homeState.data}
            liveStatus={liveStatusState}
            loading={homeState.loading}
            error={homeState.error}
            onRetry={async () => {
              setHomeState((prev) => ({ ...prev, loading: true, error: "" }));
              try {
                const data = await fetchHome();
                setHomeState({ loading: false, error: "", data });
              } catch (error) {
                setHomeState({ loading: false, error: error?.message ?? "Failed loading dashboard.", data: null });
              }
            }}
            onRetryLive={async () => {
              setLiveStatusState((prev) => ({ ...prev, loading: !prev.data, error: "" }));
              try {
                const data = await fetchLiveStatus();
                setLiveStatusState({ loading: false, error: "", data, stale: false });
              } catch (error) {
                setLiveStatusState((prev) => ({
                  ...prev,
                  loading: false,
                  error: error?.message ?? "Live status update failed.",
                  stale: Boolean(prev.data),
                }));
              }
            }}
            onOpenQueue={() => setSurface("Admin Queue")}
          />
        ) : null}

        {surface === "Members" ? (
          <MembersSurface
            rows={rosterState.rows}
            loading={rosterState.loading}
            error={rosterState.error}
            onRetry={async () => {
              setRosterState((prev) => ({ ...prev, loading: true, error: "" }));
              try {
                const data = await fetchRoster();
                const rows = Array.isArray(data?.rows) ? data.rows : [];
                setRosterState({ loading: false, error: "", rows });
              } catch (error) {
                setRosterState({ loading: false, error: error?.message ?? "Failed loading roster.", rows: [] });
              }
            }}
            onOpenProfile={(playerId) => {
              setSelectedPlayerId(playerId);
              setSurface("Player Profile");
            }}
          />
        ) : null}

        {surface === "Player Profile" ? (
          <PlayerProfileSurface
            player={profileState.data}
            loading={profileState.loading}
            error={profileState.error}
            onRetry={async () => {
              if (!selectedPlayerId) return;
              setProfileState((prev) => ({ ...prev, loading: true, error: "" }));
              try {
                const data = await fetchPlayerProfile(selectedPlayerId);
                setProfileState({ loading: false, error: "", data });
              } catch (error) {
                setProfileState({ loading: false, error: error?.message ?? "Failed loading player profile.", data: null });
              }
            }}
            onBackToMembers={() => setSurface("Members")}
          />
        ) : null}

        {surface === "Clan Log" ? (
          <ClanLogSurface
            log={clanLogState.data}
            loading={clanLogState.loading}
            error={clanLogState.error}
            onRetry={async () => {
              setClanLogState((prev) => ({ ...prev, loading: true, error: "" }));
              try {
                const data = await fetchClanLog();
                setClanLogState({ loading: false, error: "", data });
              } catch (error) {
                setClanLogState({ loading: false, error: error?.message ?? "Failed loading clan log.", data: null });
              }
            }}
          />
        ) : null}

        {surface === "Admin Queue" ? (
          <AdminQueueSurface
            cases={queueState.cases}
            selectedCase={caseDetailState.data}
            selectedCaseId={selectedCaseId}
            loading={queueState.loading}
            error={queueState.error}
            detailLoading={caseDetailState.loading}
            detailError={caseDetailState.error}
            onRetryList={async () => {
              setQueueState((prev) => ({ ...prev, loading: true, error: "" }));
              try {
                const cases = await fetchAdminQueue();
                setQueueState({ loading: false, error: "", cases: Array.isArray(cases) ? cases : [] });
              } catch (error) {
                setQueueState({ loading: false, error: error?.message ?? "Failed loading admin queue.", cases: [] });
              }
            }}
            onRetryDetail={async () => {
              if (!selectedCaseId) return;
              setCaseDetailState((prev) => ({ ...prev, loading: true, error: "" }));
              try {
                const data = await fetchAdminQueueCase(selectedCaseId);
                setCaseDetailState({ loading: false, error: "", data });
              } catch (error) {
                setCaseDetailState({ loading: false, error: error?.message ?? "Failed loading case detail.", data: null });
              }
            }}
            onSelectCase={setSelectedCaseId}
          />
        ) : null}

        {surface === "Readiness" ? (
          <ReadinessSurface
            readiness={readinessState.data}
            loading={readinessState.loading}
            error={readinessState.error}
            onRetry={async () => {
              setReadinessState((prev) => ({ ...prev, loading: true, error: "" }));
              try {
                const data = await fetchReadiness();
                setReadinessState({ loading: false, error: "", data });
              } catch (error) {
                setReadinessState({ loading: false, error: error?.message ?? "Failed loading readiness.", data: null });
              }
            }}
          />
        ) : null}
      </section>
    </main>
  );
}
