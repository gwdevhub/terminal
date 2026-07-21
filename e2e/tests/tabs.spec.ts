import { test, expect } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = dirname(fileURLToPath(import.meta.url))
const ctx = JSON.parse(readFileSync(resolve(HERE, '../.tmp/context.json'), 'utf-8')) as {
  baseUrl: string
  sshHost: string
  sshPort: number
  sshUsername: string
  sshPassword: string
}

function terminalText(page: import('@playwright/test').Page) {
  // Every open tab's terminal stays mounted (that's the point of issue #9), so there can
  // be more than one .xterm-rows in the DOM at once - scope to whichever is visible.
  return page.locator('.xterm-rows:visible').innerText()
}

async function connectNewTab(page: import('@playwright/test').Page) {
  await page.fill('#host', ctx.sshHost)
  await page.fill('#port', String(ctx.sshPort))
  await page.fill('#username', ctx.sshUsername)
  await page.fill('#password', ctx.sshPassword)
  await page.click('button[type=submit]')
  await expect(async () => {
    expect(await terminalText(page)).toContain('Welcome to OpenSSH Server')
  }).toPass({ timeout: 15_000 })
}

test('two concurrent tabs keep separate live sessions when switching between them', async ({ page }) => {
  await page.goto(ctx.baseUrl)

  await connectNewTab(page)
  const markerA = `TAB_A_${Date.now()}`
  await page.keyboard.type(`echo ${markerA}`)
  await page.keyboard.press('Enter')
  await expect(async () => {
    expect(await terminalText(page)).toContain(markerA)
  }).toPass({ timeout: 10_000 })

  // Open a second connection - the tab bar should now show two tabs plus "+".
  await page.getByRole('button', { name: 'New tab' }).click()
  await connectNewTab(page)
  const markerB = `TAB_B_${Date.now()}`
  await page.keyboard.type(`echo ${markerB}`)
  await page.keyboard.press('Enter')
  await expect(async () => {
    expect(await terminalText(page)).toContain(markerB)
  }).toPass({ timeout: 10_000 })

  // Switch back to the first tab - its output must still be there (not a fresh
  // session), and the second tab's marker must NOT leak into this one.
  const tabs = page.locator(`button:text("${ctx.sshUsername}@${ctx.sshHost}")`)
  await tabs.first().click()
  await expect(async () => {
    const text = await terminalText(page)
    expect(text).toContain(markerA)
    expect(text).not.toContain(markerB)
  }).toPass({ timeout: 5_000 })

  // Prove the first tab's session is still genuinely alive, not just showing stale
  // buffered text - run a fresh command and see it arrive live.
  const markerA2 = `TAB_A_LIVE_${Date.now()}`
  await page.keyboard.type(`echo ${markerA2}`)
  await page.keyboard.press('Enter')
  await expect(async () => {
    expect(await terminalText(page)).toContain(markerA2)
  }).toPass({ timeout: 10_000 })

  // Close the first tab - the second tab's session must be unaffected.
  await page.getByRole('button', { name: `Close ${ctx.sshUsername}@${ctx.sshHost}` }).first().click()
  await expect(async () => {
    const text = await terminalText(page)
    expect(text).toContain(markerB)
  }).toPass({ timeout: 5_000 })

  const markerB2 = `TAB_B_LIVE_${Date.now()}`
  await page.keyboard.type(`echo ${markerB2}`)
  await page.keyboard.press('Enter')
  await expect(async () => {
    expect(await terminalText(page)).toContain(markerB2)
  }).toPass({ timeout: 10_000 })
})
