const signalR = require('@microsoft/signalr');

const conn = new signalR.HubConnectionBuilder()
  .withUrl('http://JVGR22:8080/hubs/controller')
  .configureLogging(signalR.LogLevel.Warning)
  .build();

let events = [];
const record = (type, d) => { events.push({type, ts: new Date().toISOString(), ...d}); console.log('[EVT]', type, JSON.stringify(d).substring(0,150)); };

conn.on('ActionProgress', d => record('ActionProgress', d));
conn.on('GroupProgress', d => record('GroupProgress', d));
conn.on('ExecutionStarted', d => record('ExecutionStarted', d));
conn.on('ExecutionCompleted', d => record('ExecutionCompleted', d));
conn.on('LogEntry', d => record('LogEntry', d));
conn.on('AgentOutput', d => record('AgentOutput', d));
conn.on('AgentStatusChanged', d => record('AgentStatusChanged', d));
conn.on('AgentLocksChanged', d => record('AgentLocksChanged', d));
conn.on('AgentHeartbeats', d => record('AgentHeartbeats', {count: d?.length}));

async function run() {
  await conn.start();
  console.log('SignalR: Connected');
  await conn.invoke('JoinAsUser', 'monitor-test');
  console.log('SignalR: Joined as monitor-test');

  // Trigger execution
  const http = require('http');
  const triggerData = JSON.stringify({});
  const opts = { hostname: 'JVGR22', port: 8080, path: '/api/execution/trigger/ExampleTrigger', method: 'POST',
    headers: { 'Content-Type': 'application/json', 'Content-Length': triggerData.length } };

  await new Promise((resolve, reject) => {
    const req = http.request(opts, res => {
      let body = '';
      res.on('data', c => body += c);
      res.on('end', () => { console.log('Trigger response:', res.statusCode, body.substring(0,200)); resolve(); });
    });
    req.on('error', reject);
    req.write(triggerData);
    req.end();
  });

  // Wait for events to flow
  console.log('Waiting 20s for execution events...');
  await new Promise(r => setTimeout(r, 20000));

  console.log('\\n=== SUMMARY ===');
  console.log('Total events captured:', events.length);
  const byType = {};
  events.forEach(e => { byType[e.type] = (byType[e.type]||0) + 1; });
  console.log('By type:', JSON.stringify(byType));
  
  // Check Monitor-critical events
  const hasStart = events.some(e => e.type === 'ExecutionStarted');
  const hasProgress = events.some(e => e.type === 'ActionProgress');
  const hasLog = events.some(e => e.type === 'LogEntry');
  console.log('ExecutionStarted:', hasStart ? 'YES' : 'MISSING');
  console.log('ActionProgress:', hasProgress ? 'YES' : 'MISSING');
  console.log('LogEntry:', hasLog ? 'YES' : 'MISSING');

  await conn.stop();
  process.exit(0);
}

run().catch(e => { console.error('FATAL:', e.message); process.exit(1); });
