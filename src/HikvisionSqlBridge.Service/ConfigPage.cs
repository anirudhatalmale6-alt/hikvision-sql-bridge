namespace HikvisionSqlBridge.Service;

/// <summary>Página HTML (formulário) da janela de configuração. Tudo embutido — sem ficheiros externos.</summary>
public static class ConfigPage
{
    public const string Html = """
<!doctype html>
<html lang="pt">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<link rel="icon" href="/favicon.ico">
<title>SIBHIK — Configuração</title>
<style>
  :root { --azul:#1f5fa8; --azul2:#17457a; --linha:#d7dde5; --fundo:#eef1f5; --ok:#1c7c3c; --erro:#b3261e; }
  * { box-sizing:border-box; }
  body { margin:0; font-family:"Segoe UI",Tahoma,Arial,sans-serif; background:var(--fundo); color:#1a1a1a; font-size:14px; }
  header { background:var(--azul); color:#fff; padding:14px 22px; }
  header h1 { margin:0; font-size:18px; font-weight:600; }
  header p { margin:4px 0 0; font-size:12.5px; opacity:.9; }
  main { max-width:860px; margin:18px auto 90px; padding:0 16px; }
  fieldset { background:#fff; border:1px solid var(--linha); border-radius:8px; margin:0 0 16px; padding:14px 18px 18px; }
  legend { font-weight:600; color:var(--azul2); padding:0 8px; font-size:14.5px; }
  .row { display:flex; gap:14px; flex-wrap:wrap; margin:8px 0; }
  .fld { display:flex; flex-direction:column; flex:1 1 200px; }
  .fld.small { flex:0 0 140px; }
  label { font-size:12.5px; color:#444; margin-bottom:4px; }
  input[type=text], input[type=password], input[type=number], select {
    padding:7px 9px; border:1px solid #b9c2cc; border-radius:5px; font-size:13.5px; background:#fff; }
  input:focus, select:focus { outline:none; border-color:var(--azul); box-shadow:0 0 0 2px rgba(31,95,168,.15); }
  .check { display:flex; align-items:center; gap:8px; margin:8px 0; }
  .check input { width:16px; height:16px; }
  button { cursor:pointer; border:1px solid var(--azul); background:var(--azul); color:#fff;
    padding:8px 14px; border-radius:6px; font-size:13.5px; font-weight:500; }
  button:hover { background:var(--azul2); }
  button.ghost { background:#fff; color:var(--azul); }
  button.ghost:hover { background:#eef4fb; }
  button.danger { background:#fff; color:var(--erro); border-color:#e0a9a5; }
  button.danger:hover { background:#fbeceb; }
  .terminal { border:1px solid var(--linha); border-radius:7px; padding:12px 14px; margin:10px 0; background:#fafbfc; }
  .terminal .head { display:flex; justify-content:space-between; align-items:center; margin-bottom:4px; }
  .terminal .head b { color:var(--azul2); }
  .status { font-size:12.5px; margin-top:6px; min-height:16px; }
  .status.ok { color:var(--ok); }
  .status.erro { color:var(--erro); }
  .hint { font-size:12px; color:#6a7480; margin:2px 0 0; }
  .bar { position:fixed; bottom:0; left:0; right:0; background:#fff; border-top:1px solid var(--linha);
    padding:12px 22px; display:flex; gap:12px; align-items:center; }
  .bar .grow { flex:1; }
  #saveStatus { font-size:13px; }
  .adv summary { cursor:pointer; color:var(--azul2); font-size:12.5px; margin-top:6px; }
</style>
</head>
<body>
<header>
  <h1>SIBHIK — Configuração</h1>
  <p>Configure a ligação ao SQL Server e os terminais. No fim carregue em “Guardar”.</p>
</header>
<main>
  <fieldset>
    <legend>Ligação ao SQL Server</legend>
    <div class="row">
      <div class="fld"><label>Servidor / Instância</label><input type="text" id="sqlServer" placeholder="ex.: DESKTOP-PC\SQL ou 192.168.1.2,1433"></div>
      <div class="fld"><label>Base de dados</label><input type="text" id="sqlDatabase" placeholder="ex.: Assiduidadev3"></div>
      <div class="fld small"><label>Tabela das picagens</label><input type="text" id="sqlTable" placeholder="TG_MOVIMENTOS"></div>
    </div>
    <div class="check"><input type="checkbox" id="winAuth"><label for="winAuth" style="margin:0">Usar autenticação do Windows (em vez de utilizador/password)</label></div>
    <div class="row" id="sqlCreds">
      <div class="fld"><label>Utilizador</label><input type="text" id="sqlUser" placeholder="sa"></div>
      <div class="fld"><label>Password</label><input type="password" id="sqlPass"></div>
    </div>
    <div class="check"><input type="checkbox" id="sqlEncrypt"><label for="sqlEncrypt" style="margin:0">Encriptar ligação (Encrypt) — só se o servidor tiver certificado TLS válido</label></div>
    <div class="row"><button type="button" class="ghost" onclick="testSql()">Testar ligação ao SQL</button></div>
    <div class="status" id="sqlStatus"></div>
    <details class="adv">
      <summary>Avançado — colar connection string completa</summary>
      <div class="fld" style="margin-top:8px"><label>Connection string (se preenchida, é usada tal e qual)</label>
        <input type="text" id="sqlConnStr" placeholder="Data Source=...;Initial Catalog=...;User Id=...;Password=...;MultipleActiveResultSets=True"></div>
    </details>
  </fieldset>

  <fieldset>
    <legend>Terminais</legend>
    <p class="hint">Um ou vários terminais Hikvision/Safire, mesmo em redes diferentes. Cada um com o seu IP e credenciais.</p>
    <div id="terminals"></div>
    <button type="button" class="ghost" onclick="addTerminal()">+ Adicionar terminal</button>
  </fieldset>

  <fieldset>
    <legend>Sincronização de utilizadores</legend>
    <div class="check"><input type="checkbox" id="syncEnabled"><label for="syncEnabled" style="margin:0"><b>Ativar</b> sincronização automática de utilizadores</label></div>
    <div class="row">
      <div class="fld"><label>Sentido</label>
        <select id="syncDirection">
          <option value="ivms-to-sql">iVMS → SQL (do terminal para o SQL)</option>
          <option value="sql-to-ivms">SQL → iVMS (do SQL para os terminais)</option>
          <option value="both">Ambos os sentidos</option>
        </select>
      </div>
      <div class="fld small"><label>Intervalo (minutos)</label><input type="number" id="syncInterval" min="1" value="5"></div>
      <div class="fld small"><label>Validade (anos)</label><input type="number" id="syncValidity" min="1" value="10"></div>
    </div>
    <p class="hint">Só cria o que ainda não existe (pelo ID_NUMERO na TG_FUNCIONARIOS) — nunca altera dados já lá. A impressão digital/face inscreve-se no próprio terminal.</p>
    <details class="adv">
      <summary>Avançado — nomes das tabelas</summary>
      <div class="row" style="margin-top:8px">
        <div class="fld"><label>Tabela de funcionários</label><input type="text" id="syncFunc" value="TG_FUNCIONARIOS"></div>
        <div class="fld"><label>Tabela de identificadores</label><input type="text" id="syncIdent" value="TA_IDENTIFICADORES"></div>
      </div>
    </details>
  </fieldset>

  <fieldset>
    <legend>Registos (logs)</legend>
    <div class="row">
      <div class="fld small"><label>Retenção (dias)</label><input type="number" id="logRetention" min="1" value="30"></div>
      <div class="fld" style="justify-content:flex-end">
        <div class="check"><input type="checkbox" id="logRaw"><label for="logRaw" style="margin:0">Guardar eventos brutos (diagnóstico)</label></div>
      </div>
    </div>
  </fieldset>
</main>

<div class="bar">
  <button type="button" onclick="save()">Guardar</button>
  <button type="button" class="ghost" onclick="closeApp()">Fechar</button>
  <span class="grow"></span>
  <span id="saveStatus"></span>
</div>

<template id="tplTerminal">
  <div class="terminal">
    <div class="head"><b class="tname">Terminal</b><button type="button" class="danger" onclick="this.closest('.terminal').remove()">Remover</button></div>
    <div class="row">
      <div class="fld"><label>Nome</label><input type="text" class="t-name" placeholder="Porta Principal"></div>
      <div class="fld"><label>IP</label><input type="text" class="t-ip" placeholder="192.168.1.25"></div>
      <div class="fld small"><label>Porta</label><input type="number" class="t-port" value="80"></div>
    </div>
    <div class="row">
      <div class="fld"><label>Utilizador</label><input type="text" class="t-user" value="admin"></div>
      <div class="fld"><label>Password</label><input type="password" class="t-pass"></div>
      <div class="fld small"><label>Modo</label>
        <select class="t-mode"><option value="poll">Consulta (recomendado)</option><option value="stream">Streaming</option></select>
      </div>
      <div class="fld small"><label>Intervalo (seg)</label><input type="number" class="t-poll" min="1" value="3"></div>
    </div>
    <div class="check"><input type="checkbox" class="t-https"><label style="margin:0">Usar HTTPS</label></div>
    <div class="row"><button type="button" class="ghost" onclick="testTerminal(this)">Testar terminal</button></div>
    <div class="status t-status"></div>
  </div>
</template>

<script>
const $ = id => document.getElementById(id);

function addTerminal(d) {
  d = d || {};
  const node = $('tplTerminal').content.firstElementChild.cloneNode(true);
  node.querySelector('.t-name').value = d.Name || '';
  node.querySelector('.t-ip').value = d.Ip || '';
  node.querySelector('.t-port').value = d.Port || 80;
  node.querySelector('.t-user').value = d.User || 'admin';
  node.querySelector('.t-pass').value = d.Password || '';
  node.querySelector('.t-mode').value = d.Mode || 'poll';
  node.querySelector('.t-poll').value = d.PollIntervalSeconds || 3;
  node.querySelector('.t-https').checked = !!d.UseHttps;
  $('terminals').appendChild(node);
}

function readTerminal(node) {
  return {
    Name: node.querySelector('.t-name').value.trim(),
    Ip: node.querySelector('.t-ip').value.trim(),
    Port: parseInt(node.querySelector('.t-port').value) || 80,
    UseHttps: node.querySelector('.t-https').checked,
    User: node.querySelector('.t-user').value.trim(),
    Password: node.querySelector('.t-pass').value,
    Mode: node.querySelector('.t-mode').value,
    PollIntervalSeconds: parseInt(node.querySelector('.t-poll').value) || 3
  };
}

function readSql() {
  return {
    ConnectionString: $('sqlConnStr').value.trim(),
    Server: $('sqlServer').value.trim(),
    Database: $('sqlDatabase').value.trim(),
    Table: $('sqlTable').value.trim() || 'TG_MOVIMENTOS',
    UseWindowsAuth: $('winAuth').checked,
    User: $('sqlUser').value.trim(),
    Password: $('sqlPass').value,
    Encrypt: $('sqlEncrypt').checked,
    TrustServerCertificate: true
  };
}

function buildConfig() {
  const terminals = [...document.querySelectorAll('#terminals .terminal')].map(readTerminal);
  return {
    SqlServer: readSql(),
    Equipamentos: terminals,
    Logging: { Directory: 'logs', RetentionDays: parseInt($('logRetention').value) || 30, LogRawEvents: $('logRaw').checked },
    UserSync: {
      Enabled: $('syncEnabled').checked,
      Direction: $('syncDirection').value,
      IntervalMinutes: parseInt($('syncInterval').value) || 5,
      FuncionariosTable: $('syncFunc').value.trim() || 'TG_FUNCIONARIOS',
      IdentificadoresTable: $('syncIdent').value.trim() || 'TA_IDENTIFICADORES',
      ValidityYears: parseInt($('syncValidity').value) || 10
    }
  };
}

function setStatus(el, ok, msg) {
  el.className = 'status ' + (ok ? 'ok' : 'erro');
  el.textContent = (ok ? '✓ ' : '✗ ') + msg;
}

async function testSql() {
  const el = $('sqlStatus'); el.className = 'status'; el.textContent = 'A testar...';
  try {
    const r = await fetch('/api/test-sql', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(readSql()) });
    const j = await r.json(); setStatus(el, j.ok, j.message);
  } catch (e) { setStatus(el, false, 'Erro: ' + e); }
}

async function testTerminal(btn) {
  const node = btn.closest('.terminal'); const el = node.querySelector('.t-status');
  el.className = 'status'; el.textContent = 'A testar...';
  try {
    const r = await fetch('/api/test-terminal', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(readTerminal(node)) });
    const j = await r.json(); setStatus(el, j.ok, j.message);
  } catch (e) { setStatus(el, false, 'Erro: ' + e); }
}

async function save() {
  const el = $('saveStatus'); el.style.color = '#444'; el.textContent = 'A guardar...';
  try {
    const r = await fetch('/api/save', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(buildConfig()) });
    const j = await r.json();
    el.style.color = j.ok ? '#1c7c3c' : '#b3261e';
    el.textContent = (j.ok ? '✓ ' : '✗ ') + j.message;
  } catch (e) { el.style.color = '#b3261e'; el.textContent = 'Erro: ' + e; }
}

async function closeApp() {
  try { await fetch('/api/close', { method:'POST' }); } catch (e) {}
  document.body.innerHTML = '<main style="padding:40px;text-align:center;color:#444"><h2>Configuração fechada</h2><p>Já pode fechar este separador do browser.</p></main>';
}

async function load() {
  try {
    const cfg = await (await fetch('/api/config')).json();
    const s = cfg.SqlServer || {};
    $('sqlConnStr').value = s.ConnectionString || '';
    $('sqlServer').value = s.Server || '';
    $('sqlDatabase').value = s.Database || '';
    $('sqlTable').value = s.Table || 'TG_MOVIMENTOS';
    $('winAuth').checked = !!s.UseWindowsAuth;
    $('sqlUser').value = s.User || '';
    $('sqlPass').value = s.Password || '';
    $('sqlEncrypt').checked = !!s.Encrypt;
    toggleCreds();

    (cfg.Equipamentos || []).forEach(addTerminal);
    if (!(cfg.Equipamentos || []).length) addTerminal();

    const u = cfg.UserSync || {};
    $('syncEnabled').checked = !!u.Enabled;
    $('syncDirection').value = u.Direction || 'ivms-to-sql';
    $('syncInterval').value = u.IntervalMinutes || 5;
    $('syncValidity').value = u.ValidityYears || 10;
    $('syncFunc').value = u.FuncionariosTable || 'TG_FUNCIONARIOS';
    $('syncIdent').value = u.IdentificadoresTable || 'TA_IDENTIFICADORES';

    const l = cfg.Logging || {};
    $('logRetention').value = l.RetentionDays || 30;
    $('logRaw').checked = l.LogRawEvents !== false;
  } catch (e) {
    addTerminal();
  }
}

function toggleCreds() { $('sqlCreds').style.display = $('winAuth').checked ? 'none' : 'flex'; }
$('winAuth').addEventListener('change', toggleCreds);

load();
</script>
</body>
</html>
""";
}
