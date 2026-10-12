import {createBackground} from './background-core.mjs';
const worker=createBackground(chrome);
const safe=promise=>promise.catch(()=>{}); // No raw errors/payloads in diagnostics.
chrome.runtime.onInstalled.addListener(()=>safe(worker.installed()));
chrome.runtime.onStartup.addListener(()=>safe(worker.wake()));
chrome.contextMenus.onClicked.addListener((info,tab)=>safe(worker.click(info,tab)));
chrome.runtime.onMessage.addListener((message,sender,reply)=>{worker.message(message,sender).then(reply,()=>reply({phase:'cannot_confirm'}));return true;});
safe(worker.wake());
