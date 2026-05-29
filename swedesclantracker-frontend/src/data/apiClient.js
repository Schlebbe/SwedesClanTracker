const apiBase = "/api";

export async function apiGet(path) {
  const response = await fetch(`${apiBase}${path}`, {
    credentials: "include",
    headers: { "Content-Type": "application/json" },
  });

  const text = await response.text();
  if (!response.ok) {
    const details = text?.trim();
    throw new Error(details ? `Request failed ${response.status}: ${details}` : `Request failed ${response.status}`);
  }

  if (!text) return null;
  return JSON.parse(text);
}
