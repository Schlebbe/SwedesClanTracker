import { apiGet, apiPost } from "./apiClient";

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
