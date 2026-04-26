const STORAGE_KEY = 'tc-user-id';
const NAME_KEY = 'tc-user-name';

export function getUserId(): string {
  let id = localStorage.getItem(STORAGE_KEY);
  if (!id) {
    id = crypto.randomUUID().slice(0, 8);
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
