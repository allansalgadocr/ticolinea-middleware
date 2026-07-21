import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import App from './App'
import './index.css'

// basename mirrors vite.config base — the middleware serves this SPA under /admin.
//
// The trailing slash MUST be stripped. Vite's BASE_URL is "/admin/", and React
// Router matches the basename as a literal prefix: the bare URL "/admin" does
// not start with "/admin/", so the router matched nothing and rendered a blank
// page. "/admin" as the basename matches "/admin", "/admin/" and "/admin/x"
// alike. (Asset URLs are unaffected — they are absolute, from vite's base.)
const basename = import.meta.env.BASE_URL.replace(/\/+$/, '')

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter basename={basename}>
      <App />
    </BrowserRouter>
  </StrictMode>,
)
