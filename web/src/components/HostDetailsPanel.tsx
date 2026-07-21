import { useState, type FormEvent } from 'react'
import { createHost, deleteHost, type ConnectRequest, type SavedHost } from '../lib/api'

interface HostDetailsPanelProps {
  mode: 'view' | 'new' | 'empty'
  host?: SavedHost
  onConnect: (request: ConnectRequest) => void
  onDeleted: () => void
  onSaved: () => void
  onClose: () => void
}

const inputClasses =
  'w-full rounded border border-slate-700 bg-slate-900 px-3 py-2 text-slate-100 focus:border-slate-400 focus:outline-none'

// The right-hand "Host Details" panel from the Termius reference (issue #8). Full
// multi-credential editing (password/key/certificate/env var side by side) is issue #12 -
// this shows the credential list read-only for existing hosts and a single
// username/password pair when creating a new one.
export function HostDetailsPanel({ mode, host, onConnect, onDeleted, onSaved, onClose }: HostDetailsPanelProps) {
  const [name, setName] = useState('')
  const [address, setAddress] = useState('')
  const [port, setPort] = useState(22)
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)

  async function handleSave(event: FormEvent) {
    event.preventDefault()
    setError(null)
    try {
      await createHost({
        name,
        address,
        port,
        credentials: [{ id: crypto.randomUUID(), kind: 'password', username, secret: password }],
      })
      onSaved()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save host')
    }
  }

  async function handleDelete() {
    if (!host) return
    await deleteHost(host.id)
    onDeleted()
  }

  function handleConnect() {
    if (!host) return
    const credential = host.host.credentials.find((c) => c.kind === 'password')
    if (!credential) return
    onConnect({
      host: host.host.address,
      port: host.host.port,
      username: credential.username ?? '',
      authMethod: 'password',
      password: credential.secret ?? '',
      columns: 80,
      rows: 24,
    })
  }

  if (mode === 'new') {
    return (
      <div className="flex w-full flex-col gap-3 border-t border-slate-800 p-4 sm:w-80 sm:border-t-0 sm:border-l">
        <div className="flex items-center justify-between">
          <h3 className="font-semibold text-slate-100">New host</h3>
          <button type="button" onClick={onClose} className="text-slate-400 hover:text-slate-200">✕</button>
        </div>
        <form onSubmit={handleSave} className="flex flex-col gap-2">
          <input className={inputClasses} placeholder="Name" value={name} onChange={(e) => setName(e.target.value)} required />
          <div className="flex gap-2">
            <input
              className={inputClasses}
              placeholder="Address"
              value={address}
              onChange={(e) => setAddress(e.target.value)}
              required
            />
            <input
              type="number"
              className={`${inputClasses} w-20`}
              value={port}
              onChange={(e) => setPort(Number(e.target.value))}
              required
            />
          </div>
          <input
            className={inputClasses}
            placeholder="Username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            required
          />
          <input
            type="password"
            className={inputClasses}
            placeholder="Password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />
          {error && <p className="text-sm text-red-400">{error}</p>}
          <button type="submit" className="rounded bg-indigo-600 px-4 py-2 font-medium text-white hover:bg-indigo-500">
            Save host
          </button>
        </form>
      </div>
    )
  }

  if (mode === 'empty' || !host) {
    return (
      <div className="hidden w-80 shrink-0 items-center justify-center border-l border-slate-800 p-4 text-sm text-slate-500 sm:flex">
        Select a host to see its details.
      </div>
    )
  }

  return (
    <div className="flex w-full flex-col gap-4 border-t border-slate-800 p-4 sm:w-80 sm:border-t-0 sm:border-l">
      <div className="flex items-center justify-between">
        <h3 className="font-semibold text-slate-100">Host Details</h3>
        <button type="button" onClick={onClose} className="text-slate-400 hover:text-slate-200 sm:hidden">✕</button>
      </div>

      <div>
        <p className="text-xs tracking-wide text-slate-500 uppercase">Address</p>
        <p className="text-slate-100">{host.host.address}:{host.host.port}</p>
      </div>

      <div>
        <p className="text-xs tracking-wide text-slate-500 uppercase">General</p>
        <p className="text-slate-100">{host.host.name}</p>
      </div>

      <div>
        <p className="text-xs tracking-wide text-slate-500 uppercase">Credentials</p>
        <ul className="flex flex-col gap-1">
          {host.host.credentials.map((c) => (
            <li key={c.id} className="rounded border border-slate-800 bg-slate-900 px-2 py-1 text-sm text-slate-300">
              {c.kind}{c.username ? ` — ${c.username}` : ''}
            </li>
          ))}
        </ul>
      </div>

      <div className="mt-auto flex flex-col gap-2">
        <button
          type="button"
          onClick={handleConnect}
          className="rounded bg-indigo-600 px-4 py-2 font-medium text-white hover:bg-indigo-500"
        >
          Connect
        </button>
        <button
          type="button"
          onClick={handleDelete}
          className="rounded bg-slate-800 px-4 py-2 text-sm text-slate-300 hover:bg-slate-700"
        >
          Delete
        </button>
      </div>
    </div>
  )
}
