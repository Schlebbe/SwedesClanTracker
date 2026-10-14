import { apiGet, apiPost } from "./apiClient";

export function fetchSession(options) {
  return apiGet("/auth/session", options);
}

export function fetchConsoleOverview(options) {
  return apiGet("/app/console/overview", options);
}

export function fetchConsoleRoster(query = "", options) {
  return apiGet(`/app/console/roster${query ? `?${query}` : ""}`, options);
}

export function fetchConsoleProfile(playerId, range = "90d", options) {
  return apiGet(`/app/console/players/${encodeURIComponent(playerId)}/profile?range=${encodeURIComponent(range)}`, options);
}

export function fetchConsoleReviews(query = "", options) {
  return apiGet(`/app/console/reviews${query ? `?${query}` : ""}`, options);
}

export function fetchConsoleReviewDetail(caseId, options) {
  return apiGet(`/app/console/reviews/${encodeURIComponent(caseId)}`, options);
}

export function fetchIgnoredPolicies(options) {
  return apiGet("/app/console/reviews/ignored-policies", options);
}

export function fetchConsoleActivity(query = "", options) {
  return apiGet(`/app/console/activity${query ? `?${query}` : ""}`, options);
}

export function fetchConsoleSystem(options) {
  return apiGet("/app/console/system", options);
}

export async function fetchHome() {
  return apiGet("/app/home");
}

export async function fetchAdminQueue() {
  return apiGet("/app/admin-queue");
}

export async function fetchAdminQueueCase(caseId) {
  return apiGet(`/app/admin-queue/${encodeURIComponent(caseId)}`);
}

export async function fetchRoster() {
  return apiGet("/app/roster");
}

export async function fetchPlayerProfile(playerId) {
  try {
    return await apiGet(`/app/players/${playerId}/profile`);
  } catch (error) {
    if (error?.status === 404) {
      throw new Error("Player not found. It may have been removed or merged.");
    }
    throw error;
  }
}

export async function fetchClanLog() {
  return apiGet("/app/clan-log");
}

export async function fetchReadiness() {
  return apiGet("/app/readiness");
}

export async function fetchLiveStatus() {
  return apiGet("/status");
}

export async function login(username, password) {
  return apiPost("/auth/login", { username, password });
}

export async function logout() {
  return apiPost("/auth/logout");
}
