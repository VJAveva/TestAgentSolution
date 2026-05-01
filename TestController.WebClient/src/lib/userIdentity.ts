const STORAGE_KEY = 'tc-user-id';
const NAME_KEY = 'tc-user-name';

function generateId(): string {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) {
    return crypto.randomUUID().slice(0, 8);
  }
  return Math.random().toString(36).slice(2, 10);
}

export function getUserId(): string {
  let id = localStorage.getItem(STORAGE_KEY);
  if (!id) {
    id = generateId();
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
