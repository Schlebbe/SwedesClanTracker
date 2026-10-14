import { useCallback, useEffect, useState } from "react";

export function readRoute(location = window.location) {
  const pathname = location.pathname.replace(/\/+$|^$/, "") || "/";
  const query = new URLSearchParams(location.search);
  if (pathname === "/login") return { name: "login", path: pathname, query };
  if (pathname === "/") return { name: "overview", path: pathname, query };
  if (pathname === "/members") return { name: "members", path: pathname, query };
  if (pathname.startsWith("/members/")) {
    const playerId = decodeURIComponent(pathname.slice("/members/".length));
    return /^\d+$/.test(playerId) ? { name: "profile", path: pathname, playerId, query } : { name: "not-found", path: pathname, query };
  }
  if (pathname === "/reviews") return { name: "reviews", path: pathname, query };
  if (pathname.startsWith("/reviews/")) {
    const caseId = decodeURIComponent(pathname.slice("/reviews/".length));
    return caseId ? { name: "review-detail", path: pathname, caseId, query } : { name: "not-found", path: pathname, query };
  }
  if (pathname === "/activity") return { name: "activity", path: pathname, query };
  if (pathname === "/system") return { name: "system", path: pathname, query };
  return { name: "not-found", path: pathname, query };
}

export function routeHref(route, query = {}) {
  const base = typeof route === "string" ? route : route.name;
  const paths = {
    overview: "/",
    members: "/members",
    profile: `/members/${encodeURIComponent(route.playerId)}`,
    reviews: "/reviews",
    "review-detail": `/reviews/${encodeURIComponent(route.caseId)}`,
    activity: "/activity",
    system: "/system",
    login: "/login",
  };
  const path = paths[base] ?? base;
  const params = new URLSearchParams(query);
  return `${path}${params.toString() ? `?${params.toString()}` : ""}`;
}

export function useBrowserRoute() {
  const [route, setRoute] = useState(() => readRoute());
  useEffect(() => {
    const handlePopState = () => setRoute(readRoute());
    window.addEventListener("popstate", handlePopState);
    return () => window.removeEventListener("popstate", handlePopState);
  }, []);

  const navigate = useCallback((path, { replace = false } = {}) => {
    if (path === window.location.pathname + window.location.search) return;
    window.history[replace ? "replaceState" : "pushState"]({}, "", path);
    setRoute(readRoute());
    window.scrollTo({ top: 0, behavior: "instant" });
  }, []);

  return { route, navigate };
}

export function isInternalHref(href) {
  return typeof href === "string" && href.startsWith("/") && !href.startsWith("//");
}
