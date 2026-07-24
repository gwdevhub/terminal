// Optional favicon tab badge (off by default): when enabled, the browser-tab icon gets a
// small counter showing how many session tabs are open, and that counter turns the accent
// color when a background tab has produced output the user hasn't looked at yet (see
// TerminalView's onActivity / App.tsx's unseen tracking). Purely a client-side visual
// preference, so it lives in localStorage rather than the vault.

const STORAGE_KEY = 'slopterm.faviconTabBadge'

let enabled = load()
const listeners = new Set<() => void>()

function load(): boolean {
  try {
    return localStorage.getItem(STORAGE_KEY) === '1'
  } catch {
    return false
  }
}

export function isTabBadgeEnabled(): boolean {
  return enabled
}

export function setTabBadgeEnabled(value: boolean) {
  enabled = value
  try {
    localStorage.setItem(STORAGE_KEY, value ? '1' : '0')
  } catch {
    // Storage disabled - the preference just won't persist across reloads.
  }
  for (const fn of listeners) fn()
}

export function subscribeTabBadge(fn: () => void): () => void {
  listeners.add(fn)
  return () => listeners.delete(fn)
}

// --- Favicon rendering --------------------------------------------------------------------

const ICON_SIZE = 64

// The <link rel="icon"> we mutate, plus its original href/type so we can restore the plain
// favicon when the badge is turned off or there are no tabs.
let iconLink: HTMLLinkElement | null = null
let originalHref: string | null = null
let originalType: string | null = null

function ensureIconLink(): HTMLLinkElement {
  if (iconLink) return iconLink
  let link = document.querySelector<HTMLLinkElement>("link[rel~='icon']")
  if (!link) {
    link = document.createElement('link')
    link.rel = 'icon'
    document.head.appendChild(link)
  }
  iconLink = link
  originalHref = link.getAttribute('href')
  originalType = link.getAttribute('type')
  return link
}

function restoreIcon() {
  const link = ensureIconLink()
  if (originalType) link.setAttribute('type', originalType)
  else link.removeAttribute('type')
  if (originalHref) link.setAttribute('href', originalHref)
}

// The base favicon, rasterized once into an <img> we can composite the badge over. Resolves
// to null if it can't be loaded/drawn (e.g. a tainted canvas), in which case we draw a plain
// accent tile as the base so the badge still works.
let baseImagePromise: Promise<HTMLImageElement | null> | null = null
function loadBaseImage(): Promise<HTMLImageElement | null> {
  if (baseImagePromise) return baseImagePromise
  baseImagePromise = new Promise((resolve) => {
    const href = originalHref ?? '/favicon.svg'
    const img = new Image()
    img.onload = () => resolve(img)
    img.onerror = () => resolve(null)
    img.src = href
  })
  return baseImagePromise
}

function accentColor(): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue('--app-accent').trim()
  return value || '#4f46e5'
}

interface BadgeState {
  enabled: boolean
  count: number
  hasUnseen: boolean
}

let renderToken = 0

// Redraws (or restores) the favicon for the given state. Idempotent and cheap enough to call
// on every tab add/remove/activity change. Async only because the base favicon image loads
// once; a renderToken guards against an older in-flight draw landing after a newer state.
export async function applyFaviconBadge(state: BadgeState) {
  ensureIconLink()

  if (!state.enabled || state.count <= 0) {
    renderToken++ // cancel any pending draw
    restoreIcon()
    return
  }

  const token = ++renderToken
  const base = await loadBaseImage()
  if (token !== renderToken) return // a newer state superseded this draw

  const canvas = document.createElement('canvas')
  canvas.width = canvas.height = ICON_SIZE
  const ctx = canvas.getContext('2d')
  if (!ctx) return

  if (base) {
    ctx.drawImage(base, 0, 0, ICON_SIZE, ICON_SIZE)
  } else {
    // Fallback base: a rounded accent tile.
    ctx.fillStyle = accentColor()
    ctx.beginPath()
    ctx.roundRect(4, 4, ICON_SIZE - 8, ICON_SIZE - 8, 12)
    ctx.fill()
  }

  // Badge: a filled circle in the bottom-right, with a white halo so it separates from the
  // icon and any browser-tab background. Accent-colored when there's unseen activity,
  // otherwise a neutral slate so it reads as just a count.
  const r = 19
  const cx = ICON_SIZE - r - 2
  const cy = ICON_SIZE - r - 2

  ctx.beginPath()
  ctx.arc(cx, cy, r + 2.5, 0, Math.PI * 2)
  ctx.fillStyle = '#ffffff'
  ctx.fill()

  ctx.beginPath()
  ctx.arc(cx, cy, r, 0, Math.PI * 2)
  ctx.fillStyle = state.hasUnseen ? accentColor() : '#475569'
  ctx.fill()

  const label = state.count > 9 ? '9+' : String(state.count)
  ctx.fillStyle = '#ffffff'
  ctx.font = `700 ${label.length > 1 ? 26 : 32}px "Segoe UI", system-ui, sans-serif`
  ctx.textAlign = 'center'
  ctx.textBaseline = 'middle'
  ctx.fillText(label, cx, cy + 1)

  try {
    const url = canvas.toDataURL('image/png')
    const link = ensureIconLink()
    link.setAttribute('type', 'image/png')
    link.setAttribute('href', url)
  } catch {
    // Tainted canvas (shouldn't happen for the same-origin favicon) - leave the icon as-is.
    restoreIcon()
  }
}
