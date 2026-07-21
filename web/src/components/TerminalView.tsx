import { useEffect, useRef } from 'react'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { terminalSocketUrl } from '../lib/api'

interface TerminalViewProps {
  sessionId: string
  isActive: boolean
}

// Renders only the terminal itself - the tab strip (App.tsx/TabBar.tsx) owns the
// session label and close/disconnect action now that multiple sessions can be open at
// once (issue #9), so a second "Session xxx / Disconnect" header here would be redundant.
export function TerminalView({ sessionId, isActive }: TerminalViewProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const termRef = useRef<Terminal | null>(null)

  useEffect(() => {
    const container = containerRef.current
    if (!container) return

    const term = new Terminal({
      cursorBlink: true,
      fontSize: 14,
      fontFamily: 'ui-monospace, Menlo, Consolas, monospace',
    })
    const fitAddon = new FitAddon()
    term.loadAddon(fitAddon)
    term.open(container)
    fitAddon.fit()
    termRef.current = term

    const socket = new WebSocket(terminalSocketUrl(sessionId))
    socket.binaryType = 'arraybuffer'

    socket.addEventListener('open', () => term.focus())
    socket.addEventListener('message', (event) => {
      term.write(new Uint8Array(event.data as ArrayBuffer))
    })
    socket.addEventListener('close', () => {
      term.write('\r\n\x1b[31m[connection closed]\x1b[0m\r\n')
    })

    const dataDisposable = term.onData((data) => {
      if (socket.readyState === WebSocket.OPEN) {
        socket.send(new TextEncoder().encode(data))
      }
    })

    // NOTE: this only resizes the local xterm.js viewport. The backend's
    // ShellStream has a fixed size for the session (see AGENTS.md); the
    // server-side PTY does not learn about this resize yet.
    const resizeObserver = new ResizeObserver(() => fitAddon.fit())
    resizeObserver.observe(container)

    return () => {
      resizeObserver.disconnect()
      dataDisposable.dispose()
      socket.close()
      term.dispose()
      termRef.current = null
    }
  }, [sessionId])

  // Re-focus when this tab becomes the active one - it stays mounted-but-hidden while
  // inactive (see App.tsx), so nothing else would move focus back into it on tab switch.
  useEffect(() => {
    if (isActive) {
      termRef.current?.focus()
    }
  }, [isActive])

  return <div ref={containerRef} className="h-full bg-black p-1 sm:p-2" />
}
