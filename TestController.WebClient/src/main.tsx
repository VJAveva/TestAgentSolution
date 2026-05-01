import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './index.css';
import { appLogger } from './lib/logger';

// Global unhandled error capture
window.addEventListener('error', (event) => {
  appLogger.error('Global', `Uncaught: ${event.message}`, {
    filename: event.filename,
    lineno: event.lineno,
    colno: event.colno,
    stack: event.error?.stack,
  });
});

window.addEventListener('unhandledrejection', (event) => {
  const reason = event.reason;
  const message = reason?.message || reason?.error || String(reason);
  appLogger.error('Global', `Unhandled Promise: ${message}`, {
    status: reason?.status,
    detail: reason?.detail,
    correlationId: reason?.correlationId,
    stack: reason?.stack,
  });
});

appLogger.info('App', 'WebClient starting', { url: window.location.href });

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
