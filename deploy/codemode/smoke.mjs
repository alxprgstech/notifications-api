import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { Client } from '@modelcontextprotocol/sdk/client/index.js'
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js'

const apiPort = 18790
const worker = 'http://127.0.0.1:18787'
const secret = 'synthetic-notify-token-for-local-test'
let writes = 0
let denied = 0
const api = createServer((req, res) => {
  if (req.headers.authorization !== 'Bearer ' + secret) {
    res.writeHead(401).end()
    return
  }
  res.setHeader('Content-Type', 'application/json')
  if (req.url === '/api/v1/session') {
    res.end(JSON.stringify({ actor: { kind: 'integration', scopes: ['requests:read', 'notify:send'] } }))
  } else if (req.method === 'POST' && req.url === '/api/v1/notifications') {
    writes++
    res.end('{"id":"synthetic-notification"}')
  } else if (req.method === 'GET' && req.url.startsWith('/api/v1/notifications/')) {
    res.end('{"status":"pending"}')
  } else {
    denied++
    res.writeHead(404).end('{"code":"unexpected_route"}')
  }
})

async function connect(path) {
  const transport = new StreamableHTTPClientTransport(new URL(worker + path), {
    requestInit: { headers: { Authorization: 'Bearer ' + secret } },
  })
  const client = new Client({ name: 'notify-codemode-smoke', version: '1.0.0' })
  await client.connect(transport)
  return client
}

async function code(client, source) {
  return client.callTool({ name: 'code', arguments: { code: source } })
}

await new Promise(resolve => api.listen(apiPort, '127.0.0.1', resolve))
const clients = []
try {
  const anonymous = await fetch(worker + '/mcp', { method: 'POST' })
  assert.equal(anonymous.status, 401)
  const read = await connect('/mcp/read')
  clients.push(read)
  const write = await connect('/mcp')
  clients.push(write)
  const readCatalog = JSON.stringify(await read.listTools())
  const writeCatalog = JSON.stringify(await write.listTools())
  assert.match(readCatalog, /get_request/)
  assert.doesNotMatch(readCatalog, /send_notification/)
  assert.match(writeCatalog, /send_notification/)
  assert.doesNotMatch(writeCatalog, /start_call/)
  const state = await code(read, 'async () => codemode.get_request({id: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"})')
  assert.match(JSON.stringify(state), /pending/)
  const send = 'async () => codemode.send_notification({ title: "Test", body: "Smoke", idempotencyKey: "test-code-mode-1" })'
  assert.equal((await code(read, send)).isError, true)
  assert.match(JSON.stringify(await code(write, send)), /synthetic-notification/)
  const sandbox = await code(write, 'async () => { try { await fetch("https://example.com"); return "outbound_allowed" } catch { return "outbound_blocked" } }')
  assert.match(JSON.stringify(sandbox), /outbound_blocked/)
  assert.equal(writes, 1)
  assert.equal(denied, 0)
  console.log('Code Mode smoke passed: auth, scopes, read/write separation, network isolation.')
} finally {
  await Promise.allSettled(clients.map(client => client.close()))
  api.closeAllConnections()
  await new Promise(resolve => api.close(resolve))
}
