import { useEffect, useState } from "react";
import { AppShell } from "./components/shell/AppShell";
import { BeveledButton } from "./components/osrs/BeveledButton";
import { fetchAdminQueue, fetchAdminQueueCase, fetchClanLog, fetchHome, fetchLiveStatus, fetchPlayerProfile, fetchReadiness, fetchRoster, login } from "./data/appDataApi";
import { ApiError } from "./data/apiClient";
import { AdminQueueSurface } from "./surfaces/AdminQueueSurface";
import { ClanLogSurface } from "./surfaces/ClanLogSurface";
import { DashboardSurface } from "./surfaces/DashboardSurface";
import { MembersSurface } from "./surfaces/MembersSurface";
import { PlayerProfileSurface } from "./surfaces/PlayerProfileSurface";
import { ReadinessSurface } from "./surfaces/ReadinessSurface";

const authRequiredMessage = "Your session has expired. Sign in again to continue.";

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
    { id: "Dashboard", label: "Dashboard", icon: "dashboard" },
    { id: "Members", label: "Clan members", icon: "members" },
    { id: "Player Profile", label: "Player profiles", icon: "profile" },
    { id: "Admin Queue", label: "Review queues", icon: "review", badge: queueState.cases.length ? String(queueState.cases.length) : "" },
    { id: "Clan Log", label: "Activity log", icon: "activity" },
    { id: "Readiness", label: "Readiness", icon: "readiness" },
  ];

  function handleRequestError(error, fallbackMessage) {
    if (isUnauthorized(error)) {
      setAuthState({ status: "unauthenticated", message: authRequiredMessage });
      return authRequiredMessage;
    }
    return error?.message ?? fallbackMessage;
  }

  // These loaders intentionally run once when the authenticated shell mounts.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { loadHome(); loadQueue(); loadRoster(); loadClanLog(); loadReadiness(); }, []);

  useEffect(() => {
    if (surface !== "Dashboard") return undefined;
    let active = true;
    let timerId;
    const tick = async (first = false) => {
      if (first) setLiveStatusState((prev) => ({ ...prev, loading: !prev.data, error: "" }));
      try {
        const data = await fetchLiveStatus();
        if (active) setLiveStatusState({ loading: false, error: "", data, stale: false });
      } catch (error) {
        if (active) setLiveStatusState((prev) => ({ ...prev, loading: false, error: handleRequestError(error, "Tracker status update failed."), stale: Boolean(prev.data) }));
      }
      if (active) timerId = setTimeout(() => tick(), document.visibilityState === "hidden" ? 20000 : 5000);
    };
    tick(true);
    return () => { active = false; if (timerId) clearTimeout(timerId); };
  }, [surface]);

  useEffect(() => {
    if (!queueState.cases.length) { setSelectedCaseId(null); return; }
    if (!selectedCaseId || !queueState.cases.some((item) => item.id === selectedCaseId)) setSelectedCaseId(queueState.cases[0].id);
  }, [queueState.cases, selectedCaseId]);

  useEffect(() => {
    if (!selectedCaseId) { setCaseDetailState({ loading: false, error: "", data: null }); return; }
    let active = true;
    fetchAdminQueueCase(selectedCaseId).then((data) => { if (active) setCaseDetailState({ loading: false, error: "", data }); }).catch((error) => { if (active) setCaseDetailState({ loading: false, error: handleRequestError(error, "Unable to load case details."), data: null }); });
    setCaseDetailState((prev) => ({ ...prev, loading: true, error: "" }));
    return () => { active = false; };
  }, [selectedCaseId]);

  useEffect(() => {
    if (!selectedPlayerId) { setProfileState({ loading: false, error: "", data: null }); return; }
    let active = true;
    setProfileState((prev) => ({ ...prev, loading: true, error: "" }));
    fetchPlayerProfile(selectedPlayerId).then((data) => { if (active) setProfileState({ loading: false, error: "", data }); }).catch((error) => { if (active) setProfileState({ loading: false, error: handleRequestError(error, "Unable to load player profile."), data: null }); });
    return () => { active = false; };
  }, [selectedPlayerId]);

  async function loadHome() { setHomeState((prev) => ({ ...prev, loading: true, error: "" })); try { setHomeState({ loading: false, error: "", data: await fetchHome() }); } catch (error) { setHomeState({ loading: false, error: handleRequestError(error, "Unable to load dashboard."), data: null }); } }
  async function loadQueue() { setQueueState((prev) => ({ ...prev, loading: true, error: "" })); try { const data = await fetchAdminQueue(); setQueueState({ loading: false, error: "", cases: Array.isArray(data) ? data : [] }); } catch (error) { setQueueState({ loading: false, error: handleRequestError(error, "Unable to load review queues."), cases: [] }); } }
  async function loadRoster() { setRosterState((prev) => ({ ...prev, loading: true, error: "" })); try { const data = await fetchRoster(); setRosterState({ loading: false, error: "", rows: Array.isArray(data?.rows) ? data.rows : [] }); } catch (error) { setRosterState({ loading: false, error: handleRequestError(error, "Unable to load members."), rows: [] }); } }
  async function loadClanLog() { setClanLogState((prev) => ({ ...prev, loading: true, error: "" })); try { setClanLogState({ loading: false, error: "", data: await fetchClanLog() }); } catch (error) { setClanLogState({ loading: false, error: handleRequestError(error, "Unable to load activity."), data: null }); } }
  async function loadReadiness() { setReadinessState((prev) => ({ ...prev, loading: true, error: "" })); try { setReadinessState({ loading: false, error: "", data: await fetchReadiness() }); } catch (error) { setReadinessState({ loading: false, error: handleRequestError(error, "Unable to load readiness."), data: null }); } }

  async function handleLoginSubmit(event) {
    event.preventDefault();
    setLoginState({ submitting: true, error: "" });
    try { await login(loginForm.username, loginForm.password); window.location.reload(); } catch (error) { setLoginState({ submitting: false, error: isUnauthorized(error) ? "Invalid username or password." : (error?.message ?? "Sign in failed.") }); }
  }

  if (authState.status === "unauthenticated") return <LoginScreen form={loginForm} state={loginState} onChange={setLoginForm} onSubmit={handleLoginSubmit} message={authState.message} />;

  return <AppShell navItems={navItems} activeItem={surface} onSelectItem={setSurface} home={homeState.data} liveStatus={liveStatusState}>
    {surface === "Dashboard" ? <DashboardSurface data={homeState.data} liveStatus={liveStatusState} loading={homeState.loading} error={homeState.error} onRetry={loadHome} onOpenQueue={() => setSurface("Admin Queue")} onOpenMembers={() => setSurface("Members")} /> : null}
    {surface === "Members" ? <MembersSurface rows={rosterState.rows} loading={rosterState.loading} error={rosterState.error} onRetry={loadRoster} onOpenProfile={(id) => { setSelectedPlayerId(id); setSurface("Player Profile"); }} /> : null}
    {surface === "Player Profile" ? <PlayerProfileSurface player={profileState.data} loading={profileState.loading} error={profileState.error} onRetry={() => selectedPlayerId && loadProfile(selectedPlayerId)} onBackToMembers={() => setSurface("Members")} /> : null}
    {surface === "Clan Log" ? <ClanLogSurface log={clanLogState.data} loading={clanLogState.loading} error={clanLogState.error} onRetry={loadClanLog} /> : null}
    {surface === "Admin Queue" ? <AdminQueueSurface cases={queueState.cases} selectedCase={caseDetailState.data} selectedCaseId={selectedCaseId} loading={queueState.loading} error={queueState.error} detailLoading={caseDetailState.loading} detailError={caseDetailState.error} onRetryList={loadQueue} onRetryDetail={() => selectedCaseId && loadCase(selectedCaseId)} onSelectCase={setSelectedCaseId} /> : null}
    {surface === "Readiness" ? <ReadinessSurface readiness={readinessState.data} loading={readinessState.loading} error={readinessState.error} onRetry={loadReadiness} /> : null}
  </AppShell>;

  async function loadProfile(id) { setProfileState((prev) => ({ ...prev, loading: true, error: "" })); try { setProfileState({ loading: false, error: "", data: await fetchPlayerProfile(id) }); } catch (error) { setProfileState({ loading: false, error: handleRequestError(error, "Unable to load player profile."), data: null }); } }
  async function loadCase(id) { setCaseDetailState((prev) => ({ ...prev, loading: true, error: "" })); try { setCaseDetailState({ loading: false, error: "", data: await fetchAdminQueueCase(id) }); } catch (error) { setCaseDetailState({ loading: false, error: handleRequestError(error, "Unable to load case details."), data: null }); } }
}

function LoginScreen({ form, state, onChange, onSubmit, message }) {
  return <main className="auth-shell"><section className="auth-card"><div className="auth-mark"><span>SC</span></div><h1>Swedes Clan Tracker</h1><p>{message || "Sign in to continue."}</p><form onSubmit={onSubmit}><label><span>Username</span><input value={form.username} onChange={(event) => onChange((prev) => ({ ...prev, username: event.target.value }))} autoComplete="username" required /></label><label><span>Password</span><input type="password" value={form.password} onChange={(event) => onChange((prev) => ({ ...prev, password: event.target.value }))} autoComplete="current-password" required /></label>{state.error ? <p className="form-error">{state.error}</p> : null}<BeveledButton type="submit" variant="primary" loading={state.submitting}>Sign in</BeveledButton></form></section></main>;
}

function isUnauthorized(error) { return error instanceof ApiError && (error.status === 401 || error.status === 403); }
