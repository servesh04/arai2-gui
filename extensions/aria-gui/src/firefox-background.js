// Firefox MV3 event page: install listeners synchronously, then load shared modules on demand.
let worker;
const load=()=>worker??=import('./background-core.mjs').then(module=>module.createBackground(browser,true));
const safe=promise=>promise.catch(()=>{});
browser.runtime.onInstalled.addListener(()=>safe(load().then(value=>value.installed())));
browser.runtime.onStartup.addListener(()=>safe(load().then(value=>value.wake())));
browser.contextMenus.onClicked.addListener((info,tab)=>safe(load().then(value=>value.click(info,tab))));
browser.runtime.onMessage.addListener((message,sender)=>load().then(value=>value.message(message,sender)).catch(()=>({phase:'cannot_confirm'})));
safe(load().then(value=>value.wake()));
