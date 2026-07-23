import { useCallback, useEffect, useRef, useState } from 'react'
import {
  agentSocketUrl,
  getCredentialStatus,
  type AgentClientMessage,
  type AgentServerEvent,
  type ChatMessage,
  type CredentialStatus,
} from '../lib/api'
import { AiAgentIcon } from './icons'

const inputClasses =
  'w-full resize-none rounded border border-slate-700 bg-slate-900 px-3 py-2 text-sm text-slate-100 focus:border-slate-400 focus:outline-none'

// The optional AI-agent bottom region of an SSH terminal tab. It is ALWAYS present (as a
// fixed-height collapsed strip) so wrapping TerminalView in a flex column never induces an
// extra xterm fit()/redraw during first paint - only expanding it (a user action, well
// after the terminal-resize measurement window) grows the bar and re-fits the terminal via
// its existing ResizeObserver. See the pinned agent WS contract for the wire protocol.
export function AgentBar({ sessionId }: { sessionId: string }) {
  const [expanded, setExpanded] = useState(false)
  const [mode, setMode] = useState<'chat' | 'agent'>('chat')
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [input, setInput] = useState('')
  const [running, setRunning] = useState(false)
  const [credential, setCredential] = useState<CredentialStatus | null>(null)
  const [socketReady, setSocketReady] = useState(false)
  const [disconnected, setDisconnected] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)
  const [reconnectNonce, setReconnectNonce] = useState(0)

  // Held in a ref so send/stop/clear reach the live socket without re-subscribing the WS
  // effect on every render.
  const socketRef = useRef<WebSocket | null>(null)
  const transcriptRef = useRef<HTMLDivElement>(null)

  // Refresh the credential readout on mount and whenever the bar is (re-)expanded, so a key
  // just saved in Settings is reflected without a reload. Best-effort - a missing/erroring
  // endpoint just leaves the status dot neutral, never throws. Only the dot's color changes,
  // never the collapsed strip's height.
  useEffect(() => {
    let cancelled = false
    getCredentialStatus()
      .then((c) => {
        if (!cancelled) setCredential(c)
      })
      .catch(() => {
        if (!cancelled) setCredential(null)
      })
    return () => {
      cancelled = true
    }
  }, [expanded])

  // Reducer for server -> client frames. Uses only functional state updates so it never
  // captures stale `messages`, which lets it stay stable (empty dep array) and be referenced
  // from the WS effect without re-running it. MUST ignore frames whose id is not a currently
  // known assistant bubble (contract requirement - makes a late frame from a turn cancelled
  // by `clear` a harmless no-op).
  const reduce = useCallback((evt: AgentServerEvent) => {
    switch (evt.type) {
      case 'history':
        setMessages(evt.messages)
        break
      case 'turn_start':
        setMessages((prev) => [...prev, { id: evt.id, role: 'assistant', text: '', mode: evt.mode, activities: [] }])
        setRunning(true)
        break
      case 'text_delta':
        setMessages((prev) =>
          prev.some((m) => m.id === evt.id)
            ? prev.map((m) => (m.id === evt.id ? { ...m, text: m.text + evt.text } : m))
            : prev,
        )
        break
      case 'tool_activity':
        setMessages((prev) =>
          prev.some((m) => m.id === evt.id)
            ? prev.map((m) =>
                m.id === evt.id
                  ? { ...m, activities: [...m.activities, { tool: evt.tool, summary: evt.summary }] }
                  : m,
              )
            : prev,
        )
        break
      case 'turn_done': {
        setRunning(false)
        if (evt.stopReason === 'error' && evt.error) {
          const errText = evt.error
          setMessages((prev) =>
            prev.some((m) => m.id === evt.id)
              ? prev.map((m) =>
                  m.id === evt.id ? { ...m, text: m.text ? `${m.text}\n\n${errText}` : errText } : m,
                )
              : prev,
          )
        }
        break
      }
      case 'error':
        setNotice(evt.message)
        setRunning(false)
        break
    }
  }, [])

  // Only opens a socket the first time the bar is expanded (guard below); once open it stays
  // open across tab switches (an inactive tab stays mounted, so `expanded` doesn't change) so
  // agent turns keep streaming in the background. Mirrors TerminalView's socket-effect shape.
  useEffect(() => {
    if (!expanded) return
    const socket = new WebSocket(agentSocketUrl(sessionId))
    socketRef.current = socket
    socket.onopen = () => {
      setSocketReady(true)
      setDisconnected(false)
    }
    socket.onmessage = (e) => reduce(JSON.parse(e.data) as AgentServerEvent)
    socket.onerror = () => setSocketReady(false) // the browser fires close right after
    socket.onclose = () => {
      setSocketReady(false)
      setRunning(false) // no more turn frames are coming
      if (socketRef.current === socket) {
        socketRef.current = null
        setDisconnected(true)
      }
    }
    return () => {
      // Null out onclose so an unmount/collapse-initiated close does NOT flip `disconnected`.
      socket.onclose = null
      socket.close()
      if (socketRef.current === socket) socketRef.current = null
    }
  }, [sessionId, expanded, reconnectNonce, reduce])

  // Keep the transcript pinned to the newest message as text streams in.
  useEffect(() => {
    const el = transcriptRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [messages])

  function send() {
    const text = input.trim()
    const socket = socketRef.current
    if (!text || running || !socket || socket.readyState !== WebSocket.OPEN) return
    // Render the user bubble optimistically - the server records it and returns it in later
    // history snapshots, which only arrive on connect/clear, so this never double-renders.
    const userMessage: ChatMessage = { id: crypto.randomUUID(), role: 'user', text, mode, activities: [] }
    setMessages((prev) => [...prev, userMessage])
    setNotice(null)
    const frame: AgentClientMessage = { type: 'send', mode, text }
    socket.send(JSON.stringify(frame))
    setInput('')
  }

  function stop() {
    const socket = socketRef.current
    if (socket?.readyState !== WebSocket.OPEN) return
    const frame: AgentClientMessage = { type: 'stop' }
    socket.send(JSON.stringify(frame))
  }

  function clear() {
    // Local clear keeps the UI instant; the server confirms with an empty history frame.
    // setRunning(false) matches the fact that the server emits NO turn_done for the turn it
    // cancels on clear.
    setMessages([])
    setRunning(false)
    setNotice(null)
    const socket = socketRef.current
    if (socket?.readyState !== WebSocket.OPEN) return
    const frame: AgentClientMessage = { type: 'clear' }
    socket.send(JSON.stringify(frame))
  }

  function handleKeyDown(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      send()
    }
  }

  const dotColor = credential == null ? 'bg-slate-500' : credential.ready ? 'bg-emerald-500' : 'bg-amber-500'
  const sendDisabled = running || !input.trim() || !socketReady

  return (
    <div className="shrink-0 border-t border-slate-800 bg-slate-900 text-slate-200">
      {/* Collapsed strip - fixed height, always present at first paint, no transition. */}
      <div className="flex h-9 shrink-0 items-center gap-2 px-2">
        <button
          type="button"
          aria-label="AI agent"
          onClick={() => setExpanded((v) => !v)}
          className={`flex items-center gap-1.5 rounded px-2 py-1 text-xs font-medium ${
            expanded ? 'bg-indigo-600 text-white hover:bg-indigo-500' : 'text-slate-300 hover:bg-slate-800'
          }`}
        >
          <AiAgentIcon className="h-4 w-4" />
          AI agent
        </button>
        <span
          className={`h-2 w-2 shrink-0 rounded-full ${dotColor}`}
          aria-hidden="true"
          title={
            credential == null
              ? 'Checking AI credentials…'
              : credential.ready
                ? 'AI credentials ready'
                : 'No AI credentials configured'
          }
        />
        {running && <span className="text-xs text-slate-500">Working…</span>}
      </div>

      {expanded && (
        <div className="flex h-[45vh] max-h-[420px] min-h-0 w-full flex-col border-t border-slate-800">
          {/* Header row */}
          <div className="flex shrink-0 items-center gap-2 px-2 py-1.5">
            <div className="flex overflow-hidden rounded border border-slate-700">
              {(['chat', 'agent'] as const).map((m) => (
                <button
                  key={m}
                  type="button"
                  onClick={() => setMode(m)}
                  className={`px-3 py-1 text-xs font-medium capitalize ${
                    mode === m ? 'bg-indigo-600 text-white' : 'bg-slate-900 text-slate-300 hover:bg-slate-800'
                  }`}
                >
                  {m}
                </button>
              ))}
            </div>
            <div className="ml-auto flex items-center gap-1">
              <button
                type="button"
                onClick={clear}
                className="rounded px-2 py-1 text-xs text-slate-400 hover:bg-slate-800 hover:text-slate-200"
              >
                Clear chat
              </button>
              <button
                type="button"
                aria-label="Collapse AI agent"
                onClick={() => setExpanded(false)}
                className="rounded px-2 py-1 text-xs text-slate-400 hover:bg-slate-800 hover:text-slate-200"
              >
                Collapse
              </button>
            </div>
          </div>

          {credential && !credential.ready && (
            <div className="mx-2 mb-1 shrink-0 rounded border border-amber-800 bg-amber-950/40 px-3 py-2 text-xs text-amber-300">
              No Claude credentials found. Add an API key in Settings under "AI agent", or sign in with{' '}
              <code className="text-amber-200">ant auth login</code>.
            </div>
          )}

          {disconnected && (
            <div className="mx-2 mb-1 flex shrink-0 items-center justify-between gap-2 rounded border border-amber-800 bg-amber-950/40 px-3 py-2 text-xs text-amber-300">
              <span>Disconnected</span>
              <button
                type="button"
                onClick={() => {
                  setDisconnected(false)
                  setReconnectNonce((n) => n + 1)
                }}
                className="rounded bg-slate-800 px-2 py-1 text-slate-200 hover:bg-slate-700"
              >
                Reconnect
              </button>
            </div>
          )}

          {/* Transcript */}
          <div ref={transcriptRef} className="flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto px-2 py-2">
            {messages.length === 0 ? (
              <p className="m-auto max-w-xs text-center text-xs text-slate-500">
                {mode === 'agent'
                  ? 'Give the agent a goal - it can read this session and run commands you’ll see happen in the terminal.'
                  : 'Ask about what’s happening in this SSH session. Chat mode reads the terminal but never types into it.'}
              </p>
            ) : (
              messages.map((m) => <MessageBubble key={m.id} message={m} />)
            )}
          </div>

          {notice && <p className="mx-2 mb-1 shrink-0 text-xs text-red-400">{notice}</p>}

          {/* Input row */}
          <div className="flex shrink-0 items-end gap-2 border-t border-slate-800 p-2">
            <textarea
              className={inputClasses}
              rows={2}
              value={input}
              placeholder={mode === 'agent' ? 'Describe a goal…' : 'Ask a question…'}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={handleKeyDown}
            />
            <div className="flex shrink-0 flex-col gap-1">
              <button
                type="button"
                onClick={send}
                disabled={sendDisabled}
                className="rounded bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500 disabled:opacity-50"
              >
                Send
              </button>
              {running && (
                <button
                  type="button"
                  onClick={stop}
                  className="rounded bg-slate-800 px-4 py-2 text-sm font-medium text-slate-200 hover:bg-slate-700"
                >
                  Stop
                </button>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

function MessageBubble({ message }: { message: ChatMessage }) {
  const isUser = message.role === 'user'
  return (
    <div className={`flex flex-col gap-1 ${isUser ? 'items-end' : 'items-start'}`}>
      <div
        className={`max-w-[85%] whitespace-pre-wrap break-words rounded px-3 py-2 text-sm ${
          isUser ? 'bg-indigo-600 text-white' : 'bg-slate-800 text-slate-100'
        }`}
      >
        {!isUser && (
          <span className="mb-1 inline-block rounded bg-slate-700 px-1.5 py-0.5 text-[10px] uppercase tracking-wide text-slate-300">
            {message.mode}
          </span>
        )}
        {message.text && <div>{message.text}</div>}
        {message.activities.length > 0 && (
          <div className="mt-1 flex flex-col gap-1">
            {message.activities.map((a, i) => (
              <span
                key={i}
                className="flex items-center gap-1 rounded bg-slate-900/70 px-2 py-0.5 text-[11px] text-slate-400"
              >
                <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-indigo-400" aria-hidden="true" />
                {a.summary}
              </span>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
