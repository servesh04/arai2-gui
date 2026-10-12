import {readFile,mkdir,writeFile,cp} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import {fileURLToPath} from 'node:url';
const root=fileURLToPath(new URL('./',import.meta.url));
const identities=JSON.parse(await readFile(new URL('./identities.development.json',import.meta.url),'utf8'));
if(identities.channel!=='development'||!/^[a-z0-9_]+(?:\.[a-z0-9_]+)+$/.test(identities.hostName)||!identities.firefoxId.endsWith('@example.invalid'))throw new Error('Explicit development identities required');
const extensionId=key=>createHash('sha256').update(Buffer.from(key,'base64')).digest('hex').slice(0,32).replace(/[0-9a-f]/g,c=>String.fromCharCode(97+parseInt(c,16)));
const common={manifest_version:3,name:'AriaGUI public links — development',version:'0.1.0',description:'Explicit public GET link handoff. Development fixture; installer required.',permissions:['nativeMessaging','contextMenus','storage'],action:{default_popup:'popup.html'},options_ui:{page:'options.html',open_in_tab:true},incognito:'not_allowed',content_security_policy:{extension_pages:"script-src 'self'; object-src 'none'"}};
const outputs={};
for(const browser of ['chrome','edge','firefox']){
  const dir=`${root}dist/${browser}`;await mkdir(dir,{recursive:true});await cp(`${root}src`,dir,{recursive:true});
  const manifest=browser==='firefox'?{...common,background:{scripts:['firefox-background.js'],persistent:false},browser_specific_settings:{gecko:{id:identities.firefoxId,strict_min_version:'140.0',data_collection_permissions:{required:['websiteContent']}}}}:{...common,minimum_chrome_version:'120',key:identities[`${browser}Key`],background:{service_worker:'background.mjs',type:'module'}};
  await writeFile(`${dir}/manifest.json`,JSON.stringify(manifest,null,2));
  await writeFile(`${dir}/configuration.mjs`,`export const configuration=${JSON.stringify({hostName:identities.hostName})};\n`);
  const native={name:identities.hostName,description:'AriaGUI development public-link bridge',path:identities.hostExecutableTemplate,type:'stdio',...(browser==='firefox'?{allowed_extensions:[identities.firefoxId]}:{allowed_origins:[`chrome-extension://${extensionId(identities[`${browser}Key`])}/`]})};
  await writeFile(`${root}dist/${browser}.native-host.template.json`,JSON.stringify(native,null,2));outputs[browser]=browser==='firefox'?identities.firefoxId:extensionId(identities[`${browser}Key`]);
}
await writeFile(`${root}dist/identities.json`,JSON.stringify(outputs,null,2));
console.log('Generated development extension and native-manifest templates; no browser registration.');
