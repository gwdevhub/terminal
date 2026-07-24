// A tiny app-wide signal for "the vault just unlocked". Unlock happens inside whichever
// VaultGate the user hits first (VaultUnlock calls this on success), but other parts of the
// app - notably appearance sync (see App.tsx / lib/appearance.ts) - need to react to it
// without being nested under that gate. Kept deliberatly minimal: no payload, just a ping.

type Listener = () => void
const listeners = new Set<Listener>()

export function onVaultUnlocked(fn: Listener): () => void {
  listeners.add(fn)
  return () => listeners.delete(fn)
}

export function notifyVaultUnlocked() {
  for (const fn of listeners) fn()
}
