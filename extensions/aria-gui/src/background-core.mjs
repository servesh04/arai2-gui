import {Controller} from './controller.mjs';
import {NativeSession} from './transport.mjs';
import {configuration} from './configuration.mjs';
export function createBackground(api,firefox=false){
  const call=(object,method,...args)=>firefox?object[method](...args):new Promise((resolve,reject)=>object[method](...args,value=>api.runtime.lastError?reject(new Error('browser_api')):resolve(value)));
  const session=new NativeSession(name=>api.runtime.connectNative(name),configuration.hostName);
  const controller=new Controller({storage:{get:key=>call(api.storage.local,'get',key),set:value=>call(api.storage.local,'set',value)},session});
  const menu=async()=>{await call(api.contextMenus,'removeAll');const item={id:'aria-gui-public-link',title:'Download public link with AriaGUI',contexts:['link'],targetUrlPatterns:['http://*/*','https://*/*']};if(firefox)api.contextMenus.create(item);else await call(api.contextMenus,'create',item);};
  const trusted=sender=>!sender.tab&&sender.id===api.runtime.id&&(sender.url===api.runtime.getURL('popup.html')||sender.url===api.runtime.getURL('options.html'));
  return {
    installed:()=>api.extension?.inIncognitoContext?Promise.resolve({phase:'private_refused'}):menu().then(()=>controller.snapshot()),
    wake:()=>api.extension?.inIncognitoContext?Promise.resolve({phase:'private_refused'}):controller.reconcile(),
    click:(info,tab)=>api.extension?.inIncognitoContext?Promise.resolve({phase:'private_refused'}):info.menuItemId==='aria-gui-public-link'?controller.gesture(info,tab):Promise.resolve({phase:'ignored'}),
    message:async(message,sender)=>{
      if(!trusted(sender)||api.extension?.inIncognitoContext)return {phase:'refused'};
      if(message?.type==='snapshot')return controller.snapshot();
      if(message?.type==='test')return controller.test();
      if(message?.type==='reconcile')return controller.reconcile();
      if(message?.type==='configure'&&sender.url===api.runtime.getURL('options.html'))return controller.configure(message.enabled,message.installationId);
      return {phase:'refused'};
    }
  };
}
