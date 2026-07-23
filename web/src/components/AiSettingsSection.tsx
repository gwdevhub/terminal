import { useEffect, useState, type FormEvent } from 'react'
import {
  getAnthropicKeyStatus,
  getCredentialStatus,
  setAnthropicKey,
  type CredentialStatus,
} from '../lib/api'

// Duplicated verbatim (as UpdateSection does) rather than shared, so this card stays a
// self-contained clone of the GitHub-token field pattern.
const inputClasses =
  'w-full rounded border border-slate-700 bg-slate-900 px-3 py-2 text-slate-100 focus:border-slate-400 focus:outline-none'

// Human-readable readout of which credential the AI agent would actually use right now -
// derived from the SAME probe the backend gates a turn on, so it can never disagree with
// what the agent actually does.
const SOURCE_LABEL: Record<CredentialStatus['source'], string> = {
  vault: 'Using: API key (Settings vault)',
  'env-api-key': 'Using: environment variable ANTHROPIC_API_KEY',
  'env-auth-token': 'Using: environment variable ANTHROPIC_AUTH_TOKEN',
  'ant-profile': 'Using: Claude account (ant auth login)',
  none: 'Not configured',
}

// Mirrors the GitHub-token field (UpdateSection.tsx) but with DISTINCT accessible names
// (heading "AI agent", buttons "Save key"/"Clear key") so the e2e specs' exact-match
// lookups for the Updates section's Save/Clear buttons stay unambiguous.
export function AiSettingsSection() {
  const [hasKey, setHasKey] = useState<boolean | null>(null)
  const [keyInput, setKeyInput] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [credential, setCredential] = useState<CredentialStatus | null>(null)

  useEffect(() => {
    getAnthropicKeyStatus()
      .then((s) => setHasKey(s.hasKey))
      .catch(() => setHasKey(false))
    void refreshCredential()
  }, [])

  async function refreshCredential() {
    try {
      setCredential(await getCredentialStatus())
    } catch {
      setCredential(null)
    }
  }

  async function handleSave(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const result = await setAnthropicKey(keyInput || null)
      setHasKey(result.hasKey)
      setKeyInput('')
      await refreshCredential()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save key')
    } finally {
      setBusy(false)
    }
  }

  async function handleClear() {
    setBusy(true)
    setError(null)
    try {
      const result = await setAnthropicKey(null)
      setHasKey(result.hasKey)
      await refreshCredential()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to clear key')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex flex-col gap-3 rounded border border-slate-700 bg-slate-900 p-4">
      <h3 className="font-medium text-slate-100">AI agent</h3>
      <p className="text-xs text-slate-500">
        The in-terminal AI agent prefers your Claude account. Signing in with{' '}
        <code className="text-slate-400">ant auth login</code>, or setting the{' '}
        <code className="text-slate-400">ANTHROPIC_API_KEY</code> /{' '}
        <code className="text-slate-400">ANTHROPIC_AUTH_TOKEN</code> environment variables, works with no key here -
        an API key below is an optional explicit override.
        {hasKey === true && ' A key is currently set.'}
        {hasKey === false && ' No key is set yet.'}
      </p>

      <p className="text-sm text-slate-400">{SOURCE_LABEL[credential?.source ?? 'none']}</p>

      <form onSubmit={handleSave} className="flex flex-col gap-2">
        <label htmlFor="anthropic-key" className="text-sm font-medium text-slate-300">
          Anthropic API key
        </label>
        <div className="flex gap-2">
          <input
            id="anthropic-key"
            type="password"
            className={inputClasses}
            placeholder={hasKey ? '••••••••' : 'sk-ant-…'}
            value={keyInput}
            onChange={(e) => setKeyInput(e.target.value)}
          />
          <button
            type="submit"
            disabled={busy}
            className="shrink-0 rounded bg-slate-800 px-4 py-2 text-sm text-slate-300 hover:bg-slate-700 disabled:opacity-50"
          >
            Save key
          </button>
          {hasKey && (
            <button
              type="button"
              disabled={busy}
              onClick={handleClear}
              className="shrink-0 rounded bg-slate-800 px-4 py-2 text-sm text-slate-300 hover:bg-slate-700 disabled:opacity-50"
            >
              Clear key
            </button>
          )}
        </div>
        {error && <p className="text-sm text-red-400">{error}</p>}
      </form>
    </div>
  )
}
