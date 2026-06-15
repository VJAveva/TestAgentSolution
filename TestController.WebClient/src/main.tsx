import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './index.css';
import { configureAxios } from './lib/axiosConfig';
import { appLogger } from './lib/logger';

// Wire axios baseURL + correlation headers BEFORE any component mounts.
// Without this, axios calls in production go to the WebClient's own origin
// and silently fail (SPA fallback returns index.html for /api/*).
configureAxios();

// ── Global crash capture ───────────────────────────────────────────────
// Catch uncaught errors that escape React's error boundary (async throws,
// event handler blowups, third-party script failures, etc.).
window.onerror = (message, source, lineno, colno, error) => {
  appLogger.error('WindowError', `${message} at ${source}:${lineno}:${colno}`, {
    message, source, lineno, colno, stack: error?.stack,
  });
};

// Catch unhandled promise rejections (forgotten .catch(), async/await blowups).
window.onunhandledrejection = (event: PromiseRejectionEvent) => {
  const reason = event.reason;
  const message = reason instanceof Error ? reason.message : String(reason);
  appLogger.error('UnhandledRejection', message, {
    stack: reason instanceof Error ? reason.stack : undefined,
    reason,
  });
};

const rootEl = document.getElementById('root');
if (!rootEl) {
  document.body.innerHTML = '<h1 style="color:red">FATAL: #root element not found</h1>';
} else {
  const root = ReactDOM.createRoot(rootEl);
  root.render(
    <React.StrictMode>
      <App />
    </React.StrictMode>,
  );
}
