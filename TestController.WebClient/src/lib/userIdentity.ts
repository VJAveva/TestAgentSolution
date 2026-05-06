const STORAGE_KEY = 'tc-user-id';
const NAME_KEY = 'tc-user-name';

/** Generate a short random ID without requiring secure context. */
function randomId(): string {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) {
    return crypto.randomUUID().slice(0, 8);
  }
  // Fallback for insecure contexts (HTTP) where randomUUID is unavailable
  const bytes = new Uint8Array(4);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
}

export function getUserId(): string {
  let id = localStorage.getItem(STORAGE_KEY);
  if (!id) {
    id = randomId();
    localStorage.setItem(STORAGE_KEY, id);
  }
  return id;
}

export function getUserName(): string {
  return localStorage.getItem(NAME_KEY) || 'Anonymous';
}

export function setUserName(name: string) {
  localStorage.setItem(NAME_KEY, name);
}

export function isNameSet(): boolean {
  return !!localStorage.getItem(NAME_KEY);
}
