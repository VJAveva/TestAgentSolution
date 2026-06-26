/// <reference types="vitest" />
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig(({ mode }) => {
  // Load env (including non-VITE_ vars) so the dev proxy target is configurable
  // without editing this file. Lets `npm run dev` point at the WPF-embedded host
  // (5200) or a standalone WebApi on any port/host via VITE_DEV_PROXY_TARGET.
  const env = loadEnv(mode, process.cwd(), '');
  const devProxyTarget = env.VITE_DEV_PROXY_TARGET || 'http://127.0.0.1:5200';

  return {
    plugins: [react()],
    server: {
      port: 3000,
      watch: {
        ignored: ['**/.vs/**'],
      },
      proxy: {
        '/api': devProxyTarget,
        '/hubs': {
          target: devProxyTarget,
          ws: true,
        },
      },
    },
    build: {
      outDir: 'dist',
      emptyOutDir: true,
    },
    test: {
      environment: 'jsdom',
      globals: true,
      setupFiles: ['./src/test/setup.ts'],
    },
  };
});
