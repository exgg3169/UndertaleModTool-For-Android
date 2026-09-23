namespace UndertaleModTool.Core.Mcp;

/// <summary>
/// The page served at "/": connection instructions and a small console to call the MCP tools from a browser.
/// </summary>
public static class WebConsole
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>UndertaleModTool MCP</title>
<style>
  :root { --bg:#16131f; --panel:#221d30; --text:#ece8f5; --muted:#a79fbd; --accent:#ffc107; --border:#3a3150; --err:#ff6b6b; }
  @media (prefers-color-scheme: light) { :root { --bg:#f6f4fb; --panel:#fff; --text:#1d1830; --muted:#5d5575; --accent:#6a3fc9; --border:#ddd6ee; --err:#c62828; } }
  * { box-sizing:border-box; }
  body { margin:0; font:15px/1.5 system-ui, sans-serif; background:var(--bg); color:var(--text); }
  main { max-width:960px; margin:0 auto; padding:16px; }
  h1 { font-size:22px; margin:8px 0 4px; } h2 { font-size:16px; margin:20px 0 8px; color:var(--accent); }
  p, li { color:var(--muted); }
  section { background:var(--panel); border:1px solid var(--border); border-radius:10px; padding:14px; margin:12px 0; }
  code, pre, textarea, input, select { font-family:ui-monospace, Menlo, Consolas, monospace; font-size:13px; }
  pre { white-space:pre-wrap; word-break:break-word; background:rgba(0,0,0,.18); padding:10px; border-radius:8px; margin:6px 0; max-height:60vh; overflow:auto; }
  input, select, textarea { width:100%; background:var(--bg); color:var(--text); border:1px solid var(--border); border-radius:8px; padding:8px; }
  textarea { min-height:140px; }
  button { background:var(--accent); color:#111; border:0; border-radius:8px; padding:8px 14px; font-weight:600; cursor:pointer; margin:6px 6px 0 0; }
  button.secondary { background:transparent; color:var(--text); border:1px solid var(--border); }
  .row { display:flex; gap:8px; flex-wrap:wrap; align-items:center; } .row > * { flex:1 1 220px; }
  .err { color:var(--err); } .desc { color:var(--muted); font-size:13px; margin:6px 0; }
  img { max-width:100%; image-rendering:pixelated; background:repeating-conic-gradient(#8884 0 25%, transparent 0 50%) 0 0/16px 16px; border-radius:6px; }
</style>
</head>
<body>
<main>
  <h1>UndertaleModTool MCP server</h1>
  <p>This page is served by UndertaleModTool on your Android device. AI clients connect to <code id="endpoint"></code> using the access token shown in the app.</p>

  <section>
    <h2>Connect an AI client</h2>
    <p>Claude Code (terminal):</p>
    <pre id="cmd-claude"></pre>
    <p>Other clients (Claude Desktop via mcp-remote, Cursor, VS Code, ...): use this JSON config:</p>
    <pre id="cmd-json"></pre>
    <p>From a computer over USB, first run <code id="cmd-adb"></code> and use 127.0.0.1. Over Wi-Fi, enable "Allow network access" in the app and use the phone's IP.</p>
  </section>

  <section>
    <h2>Try the tools</h2>
    <div class="row">
      <input id="token" placeholder="Access token" autocomplete="off">
      <button onclick="loadTools()">Load tools</button>
    </div>
    <div class="row">
      <select id="tool" onchange="selectTool()"></select>
    </div>
    <div id="tool-desc" class="desc"></div>
    <textarea id="args" spellcheck="false">{}</textarea>
    <button onclick="callTool()">Call tool</button>
    <button class="secondary" onclick="document.getElementById('out').innerHTML=''">Clear output</button>
    <div id="out"></div>
  </section>
</main>
<script>
const endpoint = location.origin + '/mcp';
const PORT = location.port || '80';
document.getElementById('endpoint').textContent = endpoint;
const tokenBox = document.getElementById('token');
// The app opens this page with the token in the fragment (#token=...), which is never sent to the server.
try { tokenBox.value = new URLSearchParams(location.hash.slice(1)).get('token') || localStorage.getItem('umt-token') || ''; } catch (e) {}
if (location.hash) history.replaceState(null, '', location.pathname);
function renderConnect() {
  const t = tokenBox.value || '<TOKEN>';
  document.getElementById('cmd-claude').textContent =
    `claude mcp add --transport http undertalemodtool ${endpoint} --header "Authorization: Bearer ${t}"`;
  document.getElementById('cmd-json').textContent = JSON.stringify({ mcpServers: { undertalemodtool: {
    command: 'npx', args: ['-y', 'mcp-remote', endpoint, '--header', `Authorization: Bearer ${t}`] } } }, null, 2);
}
tokenBox.addEventListener('input', () => { try { localStorage.setItem('umt-token', tokenBox.value); } catch (e) {} renderConnect(); });
document.getElementById('cmd-adb').textContent = `adb forward tcp:${PORT} tcp:${PORT}`;
renderConnect();

let nextId = 1, tools = [];
async function rpc(method, params) {
  const res = await fetch(endpoint, { method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream', 'Authorization': 'Bearer ' + tokenBox.value },
    body: JSON.stringify({ jsonrpc: '2.0', id: nextId++, method, params }) });
  if (res.status === 401) throw new Error('Wrong or missing access token.');
  const json = await res.json();
  if (json.error) throw new Error(json.error.message);
  return json.result;
}
function example(schema) {
  const out = {};
  for (const [k, v] of Object.entries(schema.properties || {}))
    if ((schema.required || []).includes(k)) out[k] = v.enum ? v.enum[0] : v.type === 'integer' || v.type === 'number' ? 0 : v.type === 'boolean' ? false : '';
  return JSON.stringify(out, null, 2);
}
async function loadTools() {
  try {
    await rpc('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'web-console', version: '1' } });
    tools = (await rpc('tools/list', {})).tools;
    const sel = document.getElementById('tool');
    sel.innerHTML = tools.map((t, i) => `<option value="${i}">${t.name}</option>`).join('');
    selectTool();
  } catch (e) { show(e.message, true); }
}
function selectTool() {
  const t = tools[document.getElementById('tool').value]; if (!t) return;
  const params = Object.entries(t.inputSchema.properties || {}).map(([k, v]) => `${k}${(t.inputSchema.required || []).includes(k) ? '*' : ''}: ${v.description || ''}`).join('\n');
  document.getElementById('tool-desc').textContent = t.description + (params ? '\n' + params : '');
  document.getElementById('tool-desc').style.whiteSpace = 'pre-wrap';
  document.getElementById('args').value = example(t.inputSchema);
}
function show(text, isError) {
  const pre = document.createElement('pre'); pre.textContent = text; if (isError) pre.className = 'err';
  document.getElementById('out').prepend(pre);
}
async function callTool() {
  const t = tools[document.getElementById('tool').value]; if (!t) return show('Load the tools first.', true);
  let args; try { args = JSON.parse(document.getElementById('args').value || '{}'); } catch (e) { return show('Arguments are not valid JSON: ' + e.message, true); }
  try {
    const r = await rpc('tools/call', { name: t.name, arguments: args });
    for (const c of [...r.content].reverse()) {
      if (c.type === 'image') { const img = document.createElement('img'); img.src = `data:${c.mimeType};base64,${c.data}`; document.getElementById('out').prepend(img); }
      else show(c.text, r.isError);
    }
  } catch (e) { show(e.message, true); }
}
</script>
</body>
</html>
""";
}
