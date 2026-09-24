import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { applyTheme, readStoredTheme } from './hooks/useTheme';
import './styles/global.css';

// Apply the stored theme before the first paint so a dark-mode operator never sees a white flash.
applyTheme(readStoredTheme());

const container = document.getElementById('root');
if (!container) {
  throw new Error('Missing #root element');
}

createRoot(container).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
