const apiBase = "/api";

export class ApiError extends Error {
  constructor(status, details) {
    super(details ? `Request failed ${status}: ${details}` : `Request failed ${status}`);
    this.name = "ApiError";
    this.status = status;
  }
}

async function parseResponse(response) {
  const text = await response.text();
  if (!response.ok) {
    let details = text?.trim();
    if (text?.trim()) {
      try {
        const problem = JSON.parse(text);
        details = problem.detail || problem.title || details;
      } catch {
        // Preserve the server's text when it is not Problem Details JSON.
      }
    }
    throw new ApiError(response.status, details);
  }

  if (!text) return null;
  return JSON.parse(text);
}

export async function apiGet(path, options = {}) {
  const response = await fetch(`${apiBase}${path}`, {
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    signal: options.signal,
  });

  return parseResponse(response);
}

export async function apiPost(path, body, options = {}) {
  const response = await fetch(`${apiBase}${path}`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: options.signal,
  });

  return parseResponse(response);
}

export function isAbortError(error) {
  return error?.name === "AbortError";
}

export function isUnauthorized(error) {
  return error instanceof ApiError && (error.status === 401 || error.status === 403);
}
