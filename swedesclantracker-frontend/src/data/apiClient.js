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
    const details = text?.trim();
    throw new ApiError(response.status, details);
  }

  if (!text) return null;
  return JSON.parse(text);
}

export async function apiGet(path) {
  const response = await fetch(`${apiBase}${path}`, {
    credentials: "include",
    headers: { "Content-Type": "application/json" },
  });

  return parseResponse(response);
}

export async function apiPost(path, body) {
  const response = await fetch(`${apiBase}${path}`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  return parseResponse(response);
}
