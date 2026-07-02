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
  // per the UI-SPEC API contract shape — but that only holds for responses that
  // actually have a JSON body. A non-ok response with an empty body (e.g. the
  // 403 IsAuthorizedRequest guard) must be rejected here with a clear error
  // instead of letting res.json() throw a confusing SyntaxError.
  const text = await res.text();
  if (!res.ok && text === '') {
    throw new Error(`POST ${path} failed: ${res.status}`);
  }
  return JSON.parse(text) as T;
}
