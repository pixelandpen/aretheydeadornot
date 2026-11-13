import type { ListResponse, SearchResponse } from './types';

const API_BASE = '/api/v1';

export async function searchPerson(name: string): Promise<SearchResponse> {
  const res = await fetch(`${API_BASE}/search?name=${encodeURIComponent(name)}`);
  if (!res.ok) {
    throw new Error(`Search failed (${res.status})`);
  }
  return res.json();
}

export async function fetchPersonByQid(qid: string): Promise<SearchResponse> {
  const res = await fetch(`${API_BASE}/person/${encodeURIComponent(qid)}`);
  if (!res.ok) {
    throw new Error(`Lookup failed (${res.status})`);
  }
  return res.json();
}

export async function fetchRecentDeaths(limit = 8): Promise<ListResponse> {
  const res = await fetch(`${API_BASE}/recent-deaths?limit=${limit}`);
  if (!res.ok) {
    throw new Error(`Recent deaths failed (${res.status})`);
  }
  return res.json();
}

export async function fetchInMemory(days = 3, limit = 8): Promise<ListResponse> {
  const res = await fetch(`${API_BASE}/in-memory?days=${days}&limit=${limit}`);
  if (!res.ok) {
    throw new Error(`In memory failed (${res.status})`);
  }
  return res.json();
}
