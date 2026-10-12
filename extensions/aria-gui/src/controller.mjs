import {parse} from './protocol.mjs';
const uuid=/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
export const validId=value=>typeof value==='string'&&uuid.test(value)&&value!=='00000000-0000-0000-0000-000000000000';
const terminal=new Set(['accepted','rejected','expired']);
const phases=new Set(['disabled','not_connected','connecting','ready','sending','cannot_confirm','rejected']);
const reasons=new Set(['unsupported_protocol','invalid_request','unsupported_url','invalid_filename','destination_conflict','destination_unavailable','integration_disabled','not_ready','shutting_down','busy','storage_unavailable','request_id_conflict']);
const display=result=>result.code==='accepted'?{phase:'added',jobId:result.jobId}:result.code==='rejected'?{phase:'rejected',reason:result.reason}:{phase:'cannot_confirm'};
export class Controller {
  constructor({storage,session,id=()=>crypto.randomUUID(),now=()=>new Date().toISOString()}){Object.assign(this,{storage,session,id,now});this.queue=Promise.resolve();this.recent=new Map();}
  serial(work){const result=this.queue.then(work);this.queue=result.catch(()=>{});return result;}
  async load(){const stored=await this.storage.get('state');const raw=stored?.state??{};return {enabled:raw.enabled===true,installationId:validId(raw.installationId)?raw.installationId:'',extensionInstanceId:validId(raw.extensionInstanceId)?raw.extensionInstanceId:this.id(),pending:Array.isArray(raw.pending)?raw.pending.filter(p=>validId(p.requestId)&&validId(p.installationId)&&typeof p.createdAt==='string').slice(-32).map(p=>({requestId:p.requestId,installationId:p.installationId,createdAt:p.createdAt,state:'unknown'})):[],view:raw.view?.phase==='added'&&validId(raw.view.jobId)?{phase:'added',jobId:raw.view.jobId}: phases.has(raw.view?.phase)?{phase:raw.view.phase,...(raw.view.phase==='rejected'&&reasons.has(raw.view.reason)?{reason:raw.view.reason}:{})}:{phase:raw.enabled===true?'not_connected':'disabled'}};}
  async save(state){await this.storage.set({state});}
  envelope(action,payload,requestId=this.id()){return parse(JSON.stringify({protocol:{major:1,minor:0},requestId,action,payload}),'request');}
  async hello(state){const result=await this.session.request(this.envelope('hello',{installationId:state.installationId,extensionVersion:'0.1.0'}));if(result.code!=='ready'||!result.capabilities.includes('addPublicGet')||!result.capabilities.includes('getRequestStatus')||result.maxMessageBytes!==65536)throw new Error('unavailable');return result;}
  configure(enabled,installationId){return this.serial(async()=>{if(!validId(installationId))throw new Error('configuration');const state=await this.load();state.enabled=enabled===true;state.installationId=installationId;state.view={phase:state.enabled?'not_connected':'disabled'};await this.save(state);return state.view;});}
  snapshot(){return this.serial(async()=>{const state=await this.load();await this.save(state);return {...state.view,enabled:state.enabled,installationId:state.installationId,pendingCount:state.pending.length};});}
  test(){return this.serial(async()=>{const state=await this.load();if(!state.enabled||!validId(state.installationId))return {phase:'disabled'};try{const ready=await this.hello(state);state.view={phase:'ready',appVersion:ready.appVersion};}catch{state.view={phase:'not_connected'};}finally{this.session.close();}await this.save(state);return state.view;});}
  gesture(info,tab){return this.serial(async()=>{
    // Private contexts exit before reading/writing storage or starting a native process.
    if(info.incognito||tab?.incognito)return {phase:'private_refused'};
    const state=await this.load();if(!state.enabled||!validId(state.installationId))return {phase:'disabled'};
    let request;try{request=this.envelope('addPublicGet',{installationId:state.installationId,createdAt:this.now(),sourceUrl:info.linkUrl,trigger:'linkContextMenu'});}catch{return {phase:'unsupported_link'};}
    // Only suppress repeated callbacks in this worker, briefly; separate later user gestures get fresh IDs.
    const key=`${tab?.id??''}\n${info.linkUrl}`;const now=Date.now();if(this.recent.has(key)&&now-this.recent.get(key)<750)return state.view;
    this.recent.set(key,now);if(this.recent.size>32)this.recent.delete(this.recent.keys().next().value);
    if(state.pending.length>=32)return {phase:'busy'};
    state.view={phase:'connecting'};await this.save(state);
    try{await this.hello(state);}catch{state.view={phase:'not_connected'};await this.save(state);this.session.close();return state.view;}
    const pending={requestId:request.requestId,installationId:state.installationId,createdAt:request.payload.createdAt,state:'unknown'};
    state.pending.push(pending);state.view={phase:'sending'};
    // Persist ID before possible dispatch. No source URL is written, even before send.
    try{await this.save(state);}catch{this.session.close();throw new Error('storage');}
    try{const result=await this.session.request(request);state.view=display(result);if(terminal.has(result.code))state.pending=state.pending.filter(p=>p.requestId!==pending.requestId);}
    catch{state.view={phase:'cannot_confirm'};}finally{this.session.close();}
    await this.save(state);return state.view;
  });}
  reconcile(){return this.serial(async()=>{const state=await this.load();if(!state.enabled||!validId(state.installationId)||!state.pending.length)return state.view;
    try{await this.hello(state);for(const pending of [...state.pending].slice(0,8)){
      // v1 ledger scope is the selected APP installation, never the local extension-instance UUID.
      if(pending.installationId!==state.installationId)continue;
      const result=await this.session.request(this.envelope('getRequestStatus',{installationId:state.installationId,targetRequestId:pending.requestId}));
      state.view=display(result);state.pending=state.pending.filter(p=>p.requestId!==pending.requestId);if(!terminal.has(result.code))state.pending.push(pending);
      // not_found is not proof a late Add cannot commit. Never replay or discard ambiguity here.
      await this.save(state);
    }}catch{state.view={phase:'cannot_confirm'};}finally{this.session.close();}await this.save(state);return state.view;
  });}
}
