import { useState } from 'react'
import { AppShell } from './components/AppShell'
import { TabBar, type SessionTab } from './components/TabBar'
import { TerminalView } from './components/TerminalView'
import { connect, disconnect, type ConnectRequest } from './lib/api'

function App() {
  const [tabs, setTabs] = useState<SessionTab[]>([])
  // null = the "new connection" view (AppShell) is showing, not any particular tab.
  const [activeTabId, setActiveTabId] = useState<string | null>(null)
  const [isConnecting, setIsConnecting] = useState(false)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)

  async function handleConnect(request: ConnectRequest) {
    setIsConnecting(true)
    setErrorMessage(null)
    try {
      const response = await connect(request)
      const tab: SessionTab = { id: response.sessionId, label: `${request.username}@${request.host}` }
      setTabs((prev) => [...prev, tab])
      setActiveTabId(tab.id)
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Failed to connect')
    } finally {
      setIsConnecting(false)
    }
  }

  function handleCloseTab(id: string) {
    void disconnect(id)
    setTabs((prev) => {
      const remaining = prev.filter((t) => t.id !== id)
      setActiveTabId((current) => {
        if (current !== id) return current
        return remaining.length > 0 ? remaining[remaining.length - 1].id : null
      })
      return remaining
    })
  }

  return (
    <div className="flex h-full flex-col bg-slate-950">
      {tabs.length > 0 && (
        <TabBar
          tabs={tabs}
          activeId={activeTabId}
          onSelect={setActiveTabId}
          onClose={handleCloseTab}
          onNew={() => setActiveTabId(null)}
        />
      )}
      <div className="relative min-h-0 flex-1">
        {/* Every open tab's TerminalView stays mounted (just hidden) when inactive, so
            switching tabs doesn't tear down its WebSocket - see issue #9's requirement. */}
        {tabs.map((tab) => (
          <div key={tab.id} className={`absolute inset-0 ${activeTabId === tab.id ? 'block' : 'hidden'}`}>
            <TerminalView sessionId={tab.id} isActive={activeTabId === tab.id} />
          </div>
        ))}
        {activeTabId === null && (
          <div className="absolute inset-0 overflow-y-auto">
            <AppShell onConnect={handleConnect} errorMessage={errorMessage} isConnecting={isConnecting} />
          </div>
        )}
      </div>
    </div>
  )
}

export default App
