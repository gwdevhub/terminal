import { useState, type FormEvent } from 'react'
import { setupVault, unlockVault } from '../lib/api'
import { notifyVaultUnlocked } from '../lib/vaultEvents'

interface VaultUnlockProps {
  mode: 'setup' | 'locked'
  onUnlocked: () => void
}

export function VaultUnlock({ mode, onUnlocked }: VaultUnlockProps) {
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (mode === 'setup') {
        await setupVault(password)
      } else {
        await unlockVault(password)
      }
      setPassword('')
      // App-wide signal so appearance (and anything else outside this gate) can pull the
      // now-decryptable synced copy - see App.tsx.
      notifyVaultUnlocked()
      onUnlocked()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to unlock vault')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={handleSubmit} className="mx-auto flex w-full max-w-sm flex-col gap-3 p-4 sm:p-6">
      <h2 className="text-lg font-semibold text-slate-100">
        {mode === 'setup' ? 'Create a vault master password' : 'Unlock your vault'}
      </h2>
      <input
        type="password"
        className="w-full rounded border border-slate-700 bg-slate-900 px-3 py-2 text-slate-100 focus:border-slate-400 focus:outline-none"
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        placeholder="Master password"
        required
      />
      {error && <p className="text-sm text-red-400">{error}</p>}
      <button
        type="submit"
        disabled={busy}
        className="rounded bg-indigo-600 px-4 py-2 font-medium text-white hover:bg-indigo-500 disabled:opacity-50"
      >
        {mode === 'setup' ? 'Create vault' : 'Unlock'}
      </button>
    </form>
  )
}
