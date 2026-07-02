import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { apiGet, apiPost } from './client';

describe('apiGet', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('throws on a leading-slash path', async () => {
    await expect(apiGet('/api/sensors')).rejects.toThrow(/relative/);
  });

  it('builds a relative request and parses JSON', async () => {
    const mockJson = { entries: [] };
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => mockJson,
    });
    vi.stubGlobal('fetch', fetchMock);

    const result = await apiGet('api/sensors?q=');

    expect(fetchMock).toHaveBeenCalledWith('api/sensors?q=');
    expect(result).toEqual(mockJson);
  });

  it('throws when the response is not ok', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({ ok: false, status: 500, json: async () => ({}) })
    );
    await expect(apiGet('api/sensors')).rejects.toThrow(/500/);
  });
});

describe('apiPost', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({ ok: true, status: 200, text: async () => JSON.stringify({ ok: true }) })
    );
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('throws on a leading-slash path', async () => {
    await expect(apiPost('/api/sensors/save', {})).rejects.toThrow(/relative/);
  });

  it('sends a relative POST with JSON content-type and serialized body', async () => {
    const body = { entities: [], include: '', exclude: '' };
    await apiPost('api/sensors/save', body);

    expect(fetch).toHaveBeenCalledWith(
      'api/sensors/save',
      expect.objectContaining({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      })
    );
  });

  it('parses a JSON body on success', async () => {
    const result = await apiPost('api/sensors/save', {});
    expect(result).toEqual({ ok: true });
  });

  it('throws a clear error on a non-ok response with an empty body (e.g. 403)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({ ok: false, status: 403, text: async () => '' })
    );
    await expect(apiPost('api/sensors/save', {})).rejects.toThrow(/403/);
  });
});
