const signalR = require('@microsoft/signalr');
const conn = new signalR.HubConnectionBuilder()
  .withUrl('http://JVGR22:8080/hubs/controller')
  .configureLogging(signalR.LogLevel.Warning)
  .build();

let events = [];
conn.on('ActionProgress', d => events.push({type:'ActionProgress', ...d}));
conn.on('ExecutionStarted', d => events.push({type:'ExecutionStarted', ...d}));
conn.on('ExecutionCompleted', d => events.push({type:'ExecutionCompleted', ...d}));
conn.on('LogEntry', d => events.push({type:'LogEntry', ...d}));
conn.on('AgentHeartbeats', d => events.push({type:'AgentHeartbeats', count: d?.length}));

conn.start().then(() => {
  console.log('SignalR connected OK');
  return conn.invoke('JoinAsUser', 'test-monitor-debug');
}).then(() => {
  console.log('Joined as user: test-monitor-debug');
  // Wait 5s for any heartbeat / background events
  setTimeout(() => {
    console.log('Events received in 5s:', events.length);
    events.forEach(e => console.log(' ', JSON.stringify(e)));
    conn.stop().then(() => process.exit(0));
  }, 5000);
}).catch(err => { console.error('ERROR:', err.message); process.exit(1); });
