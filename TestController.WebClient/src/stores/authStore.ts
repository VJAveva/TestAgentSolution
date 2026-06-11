import { create } from 'zustand';
import { apiFetch } from '../lib/api';

export interface AuthUser {
  userId: string;
  username: string;
  displayName: string;
  role: string;
  clientKind: string;
  capabilities: string[];
  mustChangePassword: boolean;
  isGuest: boolean;
}

interface AuthState {
  user: AuthUser | null;
  token: string | null;
  isAuthenticated: boolean;
  mustChangePassword: boolean;
  isLoading: boolean;
  error: string | null;

  login: (username: string, password: string) => Promise<boolean>;
  loginAsGuest: () => Promise<boolean>;
  logout: () => Promise<void>;
  changePassword: (currentPassword: string, newPassword: string) => Promise<boolean>;
  fetchMe: () => Promise<void>;
  clearError: () => void;
}

const TOKEN_KEY = 'auth_token';

function getStoredToken(): string | null {
  return sessionStorage.getItem(TOKEN_KEY);
}

function storeToken(token: string): void {
  sessionStorage.setItem(TOKEN_KEY, token);
}

function clearStoredToken(): void {
  sessionStorage.removeItem(TOKEN_KEY);
}

export const useAuthStore = create<AuthState>((set, get) => ({
  user: null,
  token: getStoredToken(),
  isAuthenticated: !!getStoredToken(),
  mustChangePassword: false,
  isLoading: false,
  error: null,

  login: async (username, password) => {
    set({ isLoading: true, error: null });
    try {
      const data = await apiFetch<{ token: string; role: string; mustChangePassword: boolean }>(
        '/api/auth/login',
        {
          method: 'POST',
          body: JSON.stringify({ username, password }),
        }
      );
      storeToken(data.token);
      set({
        token: data.token,
        isAuthenticated: true,
        mustChangePassword: data.mustChangePassword,
        isLoading: false,
      });
      // Fetch full user info
      await get().fetchMe();
      return true;
    } catch (err: any) {
      set({
        isLoading: false,
        error: err.error || 'Login failed',
      });
      return false;
    }
  },

  loginAsGuest: async () => {
    set({ isLoading: true, error: null });
    try {
      const data = await apiFetch<{ token: string; role: string; mustChangePassword: boolean }>(
        '/api/auth/guest',
        { method: 'POST' }
      );
      storeToken(data.token);
      set({
        token: data.token,
        isAuthenticated: true,
        mustChangePassword: false,
        isLoading: false,
      });
      await get().fetchMe();
      return true;
    } catch (err: any) {
      set({
        isLoading: false,
        error: err.error || 'Guest login failed',
      });
      return false;
    }
  },

  logout: async () => {
    const token = get().token;
    if (token) {
      try {
        await apiFetch('/api/auth/logout', {
          method: 'POST',
          headers: { Authorization: `Bearer ${token}` },
        });
      } catch {
        // Best-effort server-side revocation
      }
    }
    clearStoredToken();
    set({
      user: null,
      token: null,
      isAuthenticated: false,
      mustChangePassword: false,
      error: null,
    });
  },

  changePassword: async (currentPassword, newPassword) => {
    const token = get().token;
    set({ isLoading: true, error: null });
    try {
      await apiFetch('/api/auth/change-password', {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}` },
        body: JSON.stringify({ currentPassword, newPassword }),
      });
      set({ mustChangePassword: false, isLoading: false });
      return true;
    } catch (err: any) {
      set({
        isLoading: false,
        error: err.error || 'Password change failed',
      });
      return false;
    }
  },

  fetchMe: async () => {
    const token = get().token;
    if (!token) return;
    try {
      const user = await apiFetch<AuthUser>('/api/auth/me', {
        headers: { Authorization: `Bearer ${token}` },
      });
      set({
        user,
        mustChangePassword: user.mustChangePassword,
        isAuthenticated: true,
      });
    } catch {
      // Token invalid or expired — clear auth state
      clearStoredToken();
      set({
        user: null,
        token: null,
        isAuthenticated: false,
      });
    }
  },

  clearError: () => set({ error: null }),
}));
