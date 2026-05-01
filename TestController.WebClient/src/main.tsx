import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './App';
import './index.css';
import { configureAxios } from './lib/axiosConfig';

// Wire axios baseURL + correlation headers BEFORE any component mounts.
// Without this, axios calls in production go to the WebClient's own origin
// and silently fail (SPA fallback returns index.html for /api/*).
configureAxios();

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>,
);
