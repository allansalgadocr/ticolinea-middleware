import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// The SPA is served by the middleware itself at /admin (UseStaticFiles +
// MapFallbackToFile), so every asset URL must be /admin-relative. Output goes
// into wwwroot/admin, which the .csproj ships inside the release artifact.
export default defineConfig({
  base: '/admin/',
  plugins: [react(), tailwindcss()],
  build: {
    outDir: '../wwwroot/admin',
    emptyOutDir: true,
  },
  server: {
    port: 5199,
    // Dev only: the SPA and the API share an origin in production (both served
    // by the middleware), so the session cookie must also look same-origin here.
    proxy: {
      '/api': {
        target: process.env.NODE_API ?? 'http://localhost:5099',
        changeOrigin: false,
      },
    },
  },
})
