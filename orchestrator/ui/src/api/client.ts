// Relative-fetch wrapper — enforces Ingress base-path safety (UI-02).
// Every /api/* call MUST go through apiGet/apiPost. Never call fetch() directly
// from components with a leading-slash path: that resolves against the origin
// root and bypasses the Supervisor Ingress prefix.

export async function apiGet<T>(path: string): Promise<T> {
  if (path.startsWith('/')) {
    throw new Error(`apiGet: path must be relative (no leading slash), got "${path}"`);
  }
  const res = await fetch(path);
  if (!res.ok) throw new Error(`GET ${path} failed: ${res.status}`);
  return res.json() as Promise<T>;
}

export async function apiPost<T>(path: string, body: unknown): Promise<T> {
  if (path.startsWith('/')) {
    throw new Error(`apiPost: path must be relative (no leading slash), got "${path}"`);
  }
  const res = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  // Callers inspect the `ok`/`kind` discriminant in the JSON body, not res.ok,
  // per the UI-SPEC API contract shape.
  return res.json() as Promise<T>;
}
