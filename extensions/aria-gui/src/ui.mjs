const api=globalThis.browser??globalThis.chrome;
const send=message=>globalThis.browser?api.runtime.sendMessage(message):new Promise((resolve,reject)=>api.runtime.sendMessage(message,value=>api.runtime.lastError?reject(new Error('unavailable')):resolve(value)));
const labels={not_connected:'Not connected. Open AriaGUI and check native host installation.',disabled:'Integration is disabled.',connecting:'Connecting to AriaGUI…',ready:'Ready for public GET links.',sending:'Sending to AriaGUI…',added:'Added to AriaGUI',rejected:'AriaGUI rejected the request.',cannot_confirm:'Cannot confirm. Check AriaGUI history before submitting again.',private_refused:'Private browsing handoff is disabled.',unsupported_link:'Only public HTTP(S) links are supported.',busy:'Too many unconfirmed requests.',refused:'This action is unavailable.'};
const reasons={destination_conflict:'The destination already exists. Rename manually in AriaGUI.',destination_unavailable:'Check the default folder in AriaGUI.',request_id_conflict:'Request identity conflict. Check app history.',integration_disabled:'Integration is disabled in AriaGUI.',unsupported_url:'This link is unsupported.',invalid_filename:'The file name is invalid.',not_ready:'AriaGUI is not ready.',shutting_down:'AriaGUI is closing.',storage_unavailable:'AriaGUI cannot save the request.'};
const status=document.getElementById('status');
function render(value){status.textContent=labels[value?.phase]??labels.cannot_confirm;if(value?.phase==='ready'&&value.appVersion)status.textContent+=` App ${value.appVersion}.`;if(value?.phase==='added'&&value.jobId)status.textContent+=` — job ${value.jobId}`;if(value?.phase==='rejected')status.textContent+=` ${reasons[value.reason]??'Check AriaGUI for details.'}`;}
async function run(message){try{const value=await send(message);render(value);return value;}catch{render({phase:'not_connected'});}}
document.getElementById('test').addEventListener('click',()=>run({type:'test'}));
document.getElementById('reconcile')?.addEventListener('click',()=>run({type:'reconcile'}));
document.getElementById('settings')?.addEventListener('click',()=>api.runtime.openOptionsPage());
const enabled=document.getElementById('enabled'),installation=document.getElementById('installation');
if(enabled){document.getElementById('save').addEventListener('click',()=>run({type:'configure',enabled:enabled.checked,installationId:installation.value.trim().toLowerCase()}));}
const snapshot=await run({type:'snapshot'});if(enabled&&snapshot){enabled.checked=snapshot.enabled;installation.value=snapshot.installationId;}
