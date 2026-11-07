
import { lazy, StrictMode, Suspense } from 'react';
import { createRoot } from 'react-dom/client';
import { Cabinet } from './cabinet';
import { ThemeProvider } from './ui';
import './style.css';

const Docs = lazy(() => import('./reference'));

function App() {
  return <ThemeProvider>
    {location.pathname.startsWith('/docs')
      ? <Suspense fallback={<p className="loading-panel" role="status">Открываем документацию…</p>}><Docs /></Suspense>
      : <Cabinet />}
  </ThemeProvider>;
}

createRoot(document.getElementById('root')!).render(<StrictMode><App /></StrictMode>);
