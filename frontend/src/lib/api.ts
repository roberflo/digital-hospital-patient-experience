export async function api<T=unknown>(path:string,method='GET',body?:unknown):Promise<T>{
 const res=await fetch('/api/crm'+path,{method,headers:body instanceof FormData?{'Idempotency-Key':crypto.randomUUID()}:{'Content-Type':'application/json','Idempotency-Key':crypto.randomUUID()},body:body instanceof FormData?body:body===undefined?undefined:JSON.stringify(body)});
 if(!res.ok){let message='No se pudo completar la operación';try{const p=await res.json();message=p.title??message;}catch{}if(res.status===401)message='Tu sesión expiró. Vuelve a iniciar sesión.';throw new Error(message);}
 if(res.status===204)return undefined as T;const text=await res.text();return (text?JSON.parse(text):undefined) as T;
}
export const fetcher=<T>(path:string)=>api<T>(path);
export type Contact={id:string;name:string;phone:string;email:string;tags:string;patientId?:string};
export type Conversation={id:string;contactId:string;status:string;assignedTo?:string;summary:string;updatedAt:string;lastInboundAt?:string;channelId:string};
export type Channel={id:string;name:string;phoneNumberId:string;doctorId?:string;coexistence:boolean;enabled:boolean};
export type Chat={conversation:Conversation;contact:Contact;channel:Channel};
export type Message={id:string;sender:string;body:string;type:string;mediaId?:string;status:string;createdAt:string};
export type Activity={id:string;body:string;actor:string;kind:string;createdAt:string;contactId?:string};
export type Member={subject:string;name:string;role:string;disabled:boolean};
export type Opportunity={id:string;title:string;contactId:string;value:number;stage:string};
export type Company={id:string;name:string;industry:string;email:string;phone:string};
export type Me={subject:string;name:string;role:string;tenant:{id:string;name:string;timeZone:string;agentEnabled:boolean}};
