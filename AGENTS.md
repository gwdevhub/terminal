# Agent instructions — slopterm

slopterm (repo: `gwdevhub/terminal`) is a cross-platform SSH/FTP terminal client in the
spirit of Termius, targeting Linux, macOS and Windows.

## Architecture

- **Front-end:** React + TypeScript + Tailwind CSS + xterm.js, built with Vite, served as
  a static bundle. Decided over a native (Avalonia) UI specifically so the terminal is
  reachable from any device's browser, not just a desktop window.
- **Mobile layout is a first-class concern, not an afterthought.** The backend serves a
  plain HTTP(S) URL, so a phone/tablet on the same network (or over a tunnel) is a
  realistic client, not an edge case. Build every screen mobile-first with Tailwind's
  responsive utilities from the start (host list, vault, SFTP browser, tab bar) — don't
  bolt on responsiveness after the desktop layout is done. The terminal view itself
  (xterm.js) is the one exception where small-screen usability is inherently limited, but
  its surrounding chrome (tabs, keyboard toolbar, connect/disconnect controls) must still
  work at phone width.
- **Backend:** .NET 8 + SSH.NET (`Renci.SshNet`) — owns all SSH/SFTP/port-forwarding I/O,
  serves the built React bundle plus a WebSocket PTY stream over a local ASP.NET Core
  (Kestrel) HTTP server.
- **No bundled browser/webview.** The backend binds `127.0.0.1` on a (by default random)
  port and prints the URL; the user opens it in whatever browser they already have
  installed — the code-server/Jupyter model. Never bind `0.0.0.0` without an explicit,
  user-supplied opt-in flag for remote access.
- **Vault:** local, per-item records (stable id + `updatedAt`) — not one monolithic blob —
  encrypted with AES-GCM behind a master password (Argon2id KDF). This shape exists so
  records can be merged across devices instead of clobbered.
- **Sync is a hard requirement**, not a stretch goal. Design is zero-knowledge: only the
  AES-GCM ciphertext ever leaves the device, the master key/password never does. Start
  with a git-backed sync backend (push/pull the encrypted blob to a private repo or gist)
  before considering a hosted sync service.

## Distribution constraints

- End users must **not** need the .NET runtime pre-installed. Publish **self-contained,
  single-file** binaries per RID (`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`).
- Do not reach for Native AOT / aggressive trimming by default. SSH.NET and typical
  JSON/DI usage rely on reflection that trimming can silently break, and chasing AOT
  compatibility adds compile-time complexity for a marginal size win. Use a plain
  self-contained (untrimmed) publish until there's a concrete reason to shrink further;
  if trimming is attempted later, explicitly re-test every reflection-touching path (SSH
  auth, SFTP, (de)serialization).
- Keep backend NuGet dependencies minimal — every package is footprint riding along with
  the CLR in a self-contained binary. Justify additions against
  SSH.NET + ASP.NET Core Kestrel + a JSON serializer before adding more.

## Security

- Backend HTTP/WebSocket port: loopback-only by default, a per-launch auth token embedded
  in the printed URL, and `Origin`/`Host` header validation on every request/upgrade to
  block DNS-rebinding and CSRF from other local browser tabs/pages.
- Never log or persist decrypted vault contents (PATs, private keys, passwords) anywhere
  outside the encrypted vault file.

## Workflow

- Base branch: `main`.
- Land all changes via pull request against `main` — no direct pushes once history exists.
