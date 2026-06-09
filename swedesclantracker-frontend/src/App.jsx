import { useEffect, useState } from "react";
import { AppShell } from "./components/shell/AppShell";
import { fetchAdminQueue, fetchAdminQueueCase, fetchClanLog, fetchHome, fetchLiveStatus, fetchPlayerProfile, fetchReadiness, fetchRoster, login } from "./data/appDataApi";
import { ApiError } from "./data/apiClient";
import { AdminQueueSurface } from "./surfaces/AdminQueueSurface";
import { ClanLogSurface } from "./surfaces/ClanLogSurface";
import { DashboardSurface } from "./surfaces/DashboardSurface";
import { MembersSurface } from "./surfaces/MembersSurface";
import { PlayerProfileSurface } from "./surfaces/PlayerProfileSurface";
import { ReadinessSurface } from "./surfaces/ReadinessSurface";

const authRequiredMessage = "Session expired. Sign in again to continue.";

function isUnauthorized(error) {
  return error instanceof ApiError && (error.status === 401 || error.status === 403);
}

export default function App() {
  const [authState, setAuthState] = useState({ status: "authenticated", message: "" });
  const [loginForm, setLoginForm] = useState({ username: "", password: "" });
  const [loginState, setLoginState] = useState({ submitting: false, error: "" });
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
  const navItems = [
    { id: "Dashboard", label: "Dashboard", icon: "D" },
    { id: "Members", label: "Clan Members", icon: "CM" },
    { id: "Player Profile", label: "Player Profiles", icon: "P" },
    { id: "Admin Queue", label: "Review Queue", icon: "RQ", badge: queueState.cases.length ? String(queueState.cases.length) : "" },
    { id: "Clan Log", label: "Activity Log", icon: "A" },
    { id: "Readiness", label: "Readiness", icon: "R" },
  ];

  function handleRequestError(error, fallbackMessage) {
    if (isUnauthorized(error)) {
      setAuthState({ status: "unauthenticated", message: authRequiredMessage });
      return authRequiredMessage;
    }

    return error?.message ?? fallbackMessage;
  }

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
        setHomeState({ loading: false, error: handleRequestError(error, "Failed loading dashboard."), data: null });
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
        setQueueState({ loading: false, error: handleRequestError(error, "Failed loading admin queue."), cases: [] });
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
        setRosterState({ loading: false, error: handleRequestError(error, "Failed loading roster."), rows: [] });
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
        setClanLogState({ loading: false, error: handleRequestError(error, "Failed loading clan log."), data: null });
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
        setReadinessState({ loading: false, error: handleRequestError(error, "Failed loading readiness."), data: null });
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
          error: handleRequestError(error, "Live status update failed."),
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
        setCaseDetailState({ loading: false, error: handleRequestError(error, "Failed loading case detail."), data: null });
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
        setProfileState({ loading: false, error: handleRequestError(error, "Failed loading player profile."), data: null });
      }
    }

    loadProfile();
    return () => {
      active = false;
    };
  }, [selectedPlayerId]);

  async function handleLoginSubmit(event) {
    event.preventDefault();
    setLoginState({ submitting: true, error: "" });
    try {
      await login(loginForm.username, loginForm.password);
      setAuthState({ status: "authenticated", message: "" });
      setLoginState({ submitting: false, error: "" });
      if (typeof window !== "undefined") {
        window.location.reload();
      }
    } catch (error) {
      const message = isUnauthorized(error) ? "Invalid username or password." : (error?.message ?? "Login failed.");
      setLoginState({ submitting: false, error: message });
    }
  }

  if (authState.status === "unauthenticated") {
    return (
      <main className="app-shell auth-shell">
        <section className="app-stage auth-stage">
          <div className="surface-grid">
            <header className="surface-header">
              <p className="eyebrow">SwedesClanTracker</p>
              <h2>Sign In Required</h2>
              <p>{authState.message || "Sign in to continue using the tracker."}</p>
            </header>

            <section className="panel">
              <form className="surface-grid" onSubmit={handleLoginSubmit}>
                <div className="toolbar auth-form-grid">
                  <input
                    value={loginForm.username}
                    onChange={(event) => setLoginForm((prev) => ({ ...prev, username: event.target.value }))}
                    placeholder="Username"
                    aria-label="Username"
                    autoComplete="username"
                    required
                  />
                  <input
                    type="password"
                    value={loginForm.password}
                    onChange={(event) => setLoginForm((prev) => ({ ...prev, password: event.target.value }))}
                    placeholder="Password"
                    aria-label="Password"
                    autoComplete="current-password"
                    required
                  />
                </div>
                {loginState.error ? <p className="tone tone-danger">{loginState.error}</p> : null}
                <div className="message-action">
                  <button className="btn-primary" type="submit" disabled={loginState.submitting}>
                    {loginState.submitting ? "Signing in..." : "Sign in"}
                  </button>
                </div>
              </form>
            </section>
          </div>
        </section>
      </main>
    );
  }

  return (
    <AppShell
      navItems={navItems}
      activeItem={surface}
      onSelectItem={setSurface}
      home={homeState.data}
      liveStatus={liveStatusState}
    >
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
                setHomeState({ loading: false, error: handleRequestError(error, "Failed loading dashboard."), data: null });
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
                  error: handleRequestError(error, "Live status update failed."),
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
                setRosterState({ loading: false, error: handleRequestError(error, "Failed loading roster."), rows: [] });
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
                setProfileState({ loading: false, error: handleRequestError(error, "Failed loading player profile."), data: null });
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
                setClanLogState({ loading: false, error: handleRequestError(error, "Failed loading clan log."), data: null });
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
                setQueueState({ loading: false, error: handleRequestError(error, "Failed loading admin queue."), cases: [] });
              }
            }}
            onRetryDetail={async () => {
              if (!selectedCaseId) return;
              setCaseDetailState((prev) => ({ ...prev, loading: true, error: "" }));
              try {
                const data = await fetchAdminQueueCase(selectedCaseId);
                setCaseDetailState({ loading: false, error: "", data });
              } catch (error) {
                setCaseDetailState({ loading: false, error: handleRequestError(error, "Failed loading case detail."), data: null });
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
                setReadinessState({ loading: false, error: handleRequestError(error, "Failed loading readiness."), data: null });
              }
            }}
          />
        ) : null}
    </AppShell>
  );
}
