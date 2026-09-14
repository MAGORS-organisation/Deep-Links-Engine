import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';

/**
 * The console is served by the control plane from wwwroot/admin (see the CopyAdminSpa target in
 * Dle.Control.csproj), so every asset URL is rooted at /admin/. Nothing is fetched from a CDN
 * (NFR-14): every script, style and font ships in this bundle.
 *
 * During development the API is proxied so that the browser talks to one origin and the
 * credential never has to cross a CORS boundary. Set VITE_DLE_CONTROL_ORIGIN in .env.local
 * when the control plane is not on http://localhost:8080.
 */
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', 'VITE_');
  const controlOrigin = env.VITE_DLE_CONTROL_ORIGIN ?? 'http://localhost:8080';

  return {
    base: '/admin/',
    plugins: [react()],
    build: {
      outDir: 'dist',
      emptyOutDir: true,
      sourcemap: false,
      target: 'es2022',
      modulePreload: { polyfill: false },
    },
    server: {
      port: 5173,
      strictPort: false,
      proxy: {
        '/api': { target: controlOrigin, changeOrigin: true },
      },
    },
  };
});
