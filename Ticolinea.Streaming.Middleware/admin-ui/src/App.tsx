import { Navigate, Route, Routes } from 'react-router-dom'
import { AuthProvider, RequireAuth, RequireOwner } from './auth'
import { Login } from './pages/Login'
import { Channels } from './pages/Channels'
import { Categories } from './pages/Categories'
import { Users } from './pages/Users'

export default function App() {
  return (
    <AuthProvider>
      <Routes>
        <Route path="/" element={<Login />} />
        <Route path="/canales" element={<RequireAuth><Channels /></RequireAuth>} />
        <Route path="/categorias" element={<RequireAuth><Categories /></RequireAuth>} />
        <Route path="/usuarios" element={<RequireOwner><Users /></RequireOwner>} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </AuthProvider>
  )
}
