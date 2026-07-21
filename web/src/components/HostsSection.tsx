import { useEffect, useState } from 'react'
import { getVaultStatus, listHosts, type ConnectRequest, type SavedHost } from '../lib/api'
import { VaultUnlock } from './VaultUnlock'
import { HostGrid } from './HostGrid'
import { HostDetailsPanel } from './HostDetailsPanel'

interface HostsSectionProps {
  onConnect: (request: ConnectRequest) => void
}

export function HostsSection({ onConnect }: HostsSectionProps) {
  const [vaultState, setVaultState] = useState<'loading' | 'setup' | 'locked' | 'unlocked'>('loading')
  const [hosts, setHosts] = useState<SavedHost[]>([])
  const [selection, setSelection] = useState<'none' | 'new' | string>('none')

  useEffect(() => {
    getVaultStatus()
      .then((s) => setVaultState(s.exists ? (s.unlocked ? 'unlocked' : 'locked') : 'setup'))
      .catch(() => setVaultState('setup'))
  }, [])

  useEffect(() => {
    if (vaultState === 'unlocked') {
      refreshHosts()
    }
  }, [vaultState])

  function refreshHosts() {
    listHosts().then(setHosts)
  }

  if (vaultState === 'loading') {
    return <p className="p-4 text-slate-400">Loading vault…</p>
  }

  if (vaultState !== 'unlocked') {
    return <VaultUnlock mode={vaultState === 'setup' ? 'setup' : 'locked'} onUnlocked={() => setVaultState('unlocked')} />
  }

  const selectedHost = selection !== 'none' && selection !== 'new' ? hosts.find((h) => h.id === selection) : undefined

  return (
    <div className="flex min-h-0 flex-1 flex-col sm:flex-row">
      <HostGrid
        hosts={hosts}
        selectedId={selection === 'new' ? null : selection === 'none' ? null : selection}
        onSelect={setSelection}
        onNewHost={() => setSelection('new')}
      />
      <HostDetailsPanel
        mode={selection === 'new' ? 'new' : selection === 'none' ? 'empty' : 'view'}
        host={selectedHost}
        onConnect={onConnect}
        onDeleted={() => {
          setSelection('none')
          refreshHosts()
        }}
        onSaved={() => {
          setSelection('none')
          refreshHosts()
        }}
        onClose={() => setSelection('none')}
      />
    </div>
  )
}
