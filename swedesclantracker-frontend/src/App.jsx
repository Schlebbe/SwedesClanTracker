import { useEffect, useMemo, useState } from "react";
import { AppShell } from "./components/shell/AppShell";
import { LoadingState } from "./components/console/Primitives";
import { ConsoleOverviewSurface } from "./surfaces/ConsoleOverviewSurface";
import { ConsoleMembersSurface } from "./surfaces/ConsoleMembersSurface";
import { ConsoleProfileSurface } from "./surfaces/ConsoleProfileSurface";
import { ConsoleReviewsQueueSurface, ConsoleReviewDetailSurface } from "./surfaces/ConsoleReviewsSurface";
import { ConsoleActivitySurface } from "./surfaces/ConsoleActivitySurface";
import { ConsoleSystemSurface } from "./surfaces/ConsoleSystemSurface";
import { ConsoleLoginSurface, ConsoleNotFoundSurface } from "./surfaces/ConsoleLoginSurface";
import { useBrowserRoute, routeHref } from "./app/router";
import { clearConsoleCache } from "./app/useConsoleRequest";
import { fetchConsoleOverview, fetchSession, login, logout } from "./data/appDataApi";
import { isAbortError, isUnauthorized } from "./data/apiClient";

const authRequiredMessage = "Your session has expired. Sign in again to continue.";

const navItems = [
  { id: "overview", route: "overview", label: "Overview", icon: "dashboard", href: "/" },
  { id: "members", route: "members", label: "Members", icon: "members", href: "/members" },
  { id: "reviews", route: "reviews", label: "Reviews", icon: "review", href: "/reviews" },
  { id: "activity", route: "activity", label: "Activity", icon: "activity", href: "/activity" },
  { id: "system", route: "system", label: "System", icon: "readiness", href: "/system" },
];

export default function App() {
  const { route, navigate } = useBrowserRoute();
  const [sessionState, setSessionState] = useState({ status: "loading", data: null, error: null });
  const [loginState, setLoginState] = useState({ submitting: false, error: "" });
  const [globalHealth, setGlobalHealth] = useState(null);
  const [globalHealthError, setGlobalHealthError] = useState(null);

  useEffect(() => {
    const controller = new AbortController();
    fetchSession({ signal: controller.signal })
      .then((data) => setSessionState({ status: "authenticated", data, error: null }))
      .catch((error) => {
        if (controller.signal.aborted || isAbortError(error)) return;
        setSessionState({ status: isUnauthorized(error) ? "unauthenticated" : "error", data: null, error });
      });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (sessionState.status !== "authenticated") return undefined;
    let active = true;
    let timer;
    const load = async () => {
      const controller = new AbortController();
      try {
        const data = await fetchConsoleOverview({ signal: controller.signal });
        if (active) {
          setGlobalHealth(data);
          setGlobalHealthError(null);
        }
      } catch (error) {
        if (active && !isAbortError(error)) setGlobalHealthError(error);
      }
      if (active) timer = window.setTimeout(load, document.visibilityState === "hidden" ? 60_000 : 30_000);
    };
    load();
    return () => {
      active = false;
      if (timer) window.clearTimeout(timer);
    };
  }, [sessionState.status]);

  useEffect(() => {
    if (sessionState.status === "unauthenticated" && route.name !== "login") {
      const returnTo = `${route.path}${window.location.search}`;
      navigate(routeHref("login", { returnTo }), { replace: true });
    }
    if (sessionState.status === "authenticated" && route.name === "login") {
      const returnTo = route.query.get("returnTo");
      navigate(returnTo?.startsWith("/") ? returnTo : "/", { replace: true });
    }
  }, [navigate, route, sessionState.status]);

  async function handleLogin(username, password) {
    setLoginState({ submitting: true, error: "" });
    try {
      await login(username, password);
      const data = await fetchSession();
      setSessionState({ status: "authenticated", data, error: null });
      const returnTo = route.query.get("returnTo");
      navigate(returnTo?.startsWith("/") ? returnTo : "/", { replace: true });
      setLoginState({ submitting: false, error: "" });
    } catch (error) {
      setLoginState({ submitting: false, error: isUnauthorized(error) ? "Invalid username or password." : error.message });
    }
  }

  async function handleLogout() {
    try {
      await logout();
    } catch {
      // A local session reset is still safer when the server session has already expired.
    }
    clearConsoleCache();
    setGlobalHealth(null);
    setSessionState({ status: "unauthenticated", data: null, error: null });
    navigate(routeHref("login", { returnTo: "/" }), { replace: true });
  }

  const content = useMemo(() => {
    if (route.name === "overview") return <ConsoleOverviewSurface onNavigate={navigate} />;
    if (route.name === "members") return <ConsoleMembersSurface route={route} onNavigate={navigate} />;
    if (route.name === "profile") return <ConsoleProfileSurface route={route} onNavigate={navigate} />;
    if (route.name === "reviews") return <ConsoleReviewsQueueSurface route={route} onNavigate={navigate} />;
    if (route.name === "review-detail") return <ConsoleReviewDetailSurface route={route} onNavigate={navigate} />;
    if (route.name === "activity") return <ConsoleActivitySurface route={route} onNavigate={navigate} />;
    if (route.name === "system") return <ConsoleSystemSurface route={route} onNavigate={navigate} />;
    return <ConsoleNotFoundSurface onNavigate={navigate} />;
  }, [navigate, route]);

  if (sessionState.status === "loading") return <main className="boot-shell"><LoadingState label="Checking authenticated session…" /></main>;
  if (sessionState.status === "error") return <main className="boot-shell"><div className="state-box state-error" role="alert"><strong>Session bootstrap unavailable</strong><span>{sessionState.error?.message ?? "The API session endpoint could not be reached."}</span><button type="button" className="text-button" onClick={() => window.location.reload()}>Retry</button></div></main>;
  if (sessionState.status === "unauthenticated" || route.name === "login") {
    const returnTo = route.query.get("returnTo") ?? (route.name !== "login" ? `${route.path}${window.location.search}` : null);
    return <ConsoleLoginSurface returnTo={returnTo} submitting={loginState.submitting} error={loginState.error || (sessionState.status === "unauthenticated" && route.name !== "login" ? authRequiredMessage : "")} onSubmit={handleLogin} />;
  }

  return <AppShell navItems={navItems} route={route} session={sessionState.data} health={globalHealth?.health} onNavigate={navigate} onLogout={handleLogout}>{globalHealthError && route.name === "overview" ? <div className="global-health-note">Health poll unavailable; page data may still be current.</div> : null}{content}</AppShell>;
}
