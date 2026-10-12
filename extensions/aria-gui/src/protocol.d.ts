export type UUID = string;
export type Action = 'hello' | 'addPublicGet' | 'getRequestStatus';
export type Request = {protocol:{major:1;minor:0};requestId:UUID;action:Action;payload:Hello|PublicGet|Status};
export type Hello = {installationId:UUID;extensionVersion:string};
export type PublicGet = {installationId:UUID;createdAt:string;sourceUrl:string;trigger:'linkContextMenu';suggestedFileName?:string};
export type Status = {installationId:UUID;targetRequestId:UUID};
export type Result = {code:'ready';appVersion:string;capabilities:string[];maxMessageBytes:number}
 | {code:'accepted';jobId:UUID;state:string;duplicate:boolean}
 | {code:'rejected';reason:string} | {code:'processing';retryAfterMs:number}
 | {code:'not_found'|'expired'|'unavailable'|'unknown'};
export type Response = {protocol:{major:1;minor:number};requestId:UUID;result:Result};
export function parse(text:string,kind:'request'):Request;
export function parse(text:string,kind:'response'):Response;
