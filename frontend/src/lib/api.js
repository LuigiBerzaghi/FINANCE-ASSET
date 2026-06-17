const BASE = '/api';
const TOKEN_KEY = 'pucfinance.authToken';

export function getAuthToken() {
  return localStorage.getItem(TOKEN_KEY);
}

export function setAuthToken(token) {
  localStorage.setItem(TOKEN_KEY, token);
}

export function clearAuthToken() {
  localStorage.removeItem(TOKEN_KEY);
}

function authHeaders(extra = {}) {
  const token = getAuthToken();
  return token ? { ...extra, Authorization: `Bearer ${token}` } : extra;
}

export async function get(path) {
  const r = await fetch(`${BASE}${path}`, {
    headers: authHeaders(),
  });
  if (!r.ok) {
    const text = await r.text();
    const error = new Error(text || `HTTP ${r.status}`);
    error.status = r.status;
    throw error;
  }
  return r.json();
}

export async function post(path, body) {
  const r = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify(body),
  });
  const data = await r.json().catch(() => ({}));
  if (!r.ok) {
    const error = new Error(data.error || `HTTP ${r.status}`);
    error.status = r.status;
    throw error;
  }
  return data;
}

export async function del(path) {
  const r = await fetch(`${BASE}${path}`, {
    method: 'DELETE',
    headers: authHeaders(),
  });
  const data = await r.json().catch(() => ({}));
  if (!r.ok) {
    const error = new Error(data.error || `HTTP ${r.status}`);
    error.status = r.status;
    throw error;
  }
  return data;
}

export async function download(path) {
  const r = await fetch(`${BASE}${path}`, {
    headers: authHeaders(),
  });
  if (!r.ok) {
    const text = await r.text();
    const error = new Error(text || `HTTP ${r.status}`);
    error.status = r.status;
    throw error;
  }
  return r.blob();
}
