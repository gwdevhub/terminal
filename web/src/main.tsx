import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { applyAppearance, loadAppearance } from './lib/appearance.ts'

// Apply the saved colors/fonts before the first render so there's no flash of the default
// theme (the CSS defaults in index.css already match stock Tailwind, so an unset install
// looks identical; a customized one themes immediately).
applyAppearance(loadAppearance())

// Registering a service worker is one of the installability requirements for "Add as
// PWA" - see public/sw.js for why it deliberately does no caching.
if ('serviceWorker' in navigator) {
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js')
  })
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
