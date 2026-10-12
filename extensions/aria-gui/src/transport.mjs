import {parse} from './protocol.mjs';
// Native APIs deliver objects, not binary frames. Validate closed shape and correlation before use.
export class NativeSession {
  constructor(connect, host, timeout=20000) { this.connect=connect;this.host=host;this.timeout=timeout;this.port=null;this.pending=null; }
  /** @param {import('./protocol.mjs').Request} request @returns {Promise<import('./protocol.mjs').Result>} */
  async request(request) {
    if(this.pending) throw new Error('busy');
    parse(JSON.stringify(request),'request');
    if(!this.port) {
      this.port=this.connect(this.host);
      this.port.onMessage.addListener(value=>{
        const pending=this.pending;if(!pending)return;
        try { const reply=parse(JSON.stringify(value),'response');if(reply.requestId!==pending.id)throw new Error('correlation');pending.resolve(reply.result); }
        catch { pending.reject(new Error('invalid_response'));this.close(); }
      });
      this.port.onDisconnect.addListener(()=>{void globalThis.chrome?.runtime?.lastError;this.pending?.reject(new Error('disconnected'));this.port=null;});
    }
    return new Promise((resolve,reject)=>{
      const finish=callback=>value=>{clearTimeout(timer);this.pending=null;callback(value);};
      const timer=setTimeout(()=>{this.pending?.reject(new Error('timeout'));this.close();},this.timeout);
      this.pending={id:request.requestId,resolve:finish(resolve),reject:finish(reject)};
      try{this.port.postMessage(request);}catch{this.pending.reject(new Error('disconnected'));this.close();}
    });
  }
  close(){const port=this.port;this.port=null;this.pending?.reject(new Error('closed'));if(port)try{port.disconnect();}catch{}}
}
