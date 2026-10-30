import fs from 'node:fs/promises';
import crypto from 'node:crypto';
const definitions = [['news',5211],['ir',5212],['market',5213]];
let id=0;
async function call(port,method,params={}) {
  const r=await fetch(`http://127.0.0.1:${port}/mcp`,{method:'POST',headers:{'Content-Type':'application/json','Accept':'application/json, text/event-stream'},body:JSON.stringify({jsonrpc:'2.0',id:++id,method,params})});
  const body=await r.text();
  if(!r.ok) throw new Error(`MCP ${r.status}: ${body.slice(0,300)}`);
  const payload=body.startsWith('event:')||body.startsWith('data:') ? body.split('\n').filter(l=>l.startsWith('data:')).map(l=>JSON.parse(l.slice(5))).at(-1) : JSON.parse(body);
  if(payload.error) throw new Error(JSON.stringify(payload.error));
  return payload.result;
}
function canonical(x) {if(Array.isArray(x))return x.map(canonical); if(x&&typeof x==='object')return Object.fromEntries(Object.keys(x).sort().map(k=>[k,canonical(x[k])])); return x;}
const contracts={};
for(const [name,port] of definitions) {
 await call(port,'initialize',{protocolVersion:'2025-11-25',clientInfo:{name:'lges-ama-validation',version:'1.0'},capabilities:{}});
 const listed=await call(port,'tools/list');
 contracts[name]=listed.tools.map(t=>({Name:t.name,Description:t.description,Parameters:Object.keys(t.inputSchema.properties??{}),RequiredParameters:t.inputSchema.required??[],SchemaSha256:crypto.createHash('sha256').update(JSON.stringify(canonical(t.inputSchema))).digest('hex').toUpperCase()}));
 console.log(name, listed.tools.map(t=>t.name).join(', '));
}
if(process.argv.includes('--write-contracts')) await fs.writeFile('contracts/mcp-contracts.json',JSON.stringify(contracts,null,2)+'\n');
if(process.argv.includes('--live')) {
 const checks = [[5213,'get_stock_quote',{}],[5212,'get_company_overview',{}],[5212,'get_ir_materials',{year:2026,limit:4}],[5211,'search_company_news',{query:'실적',days:90,limit:4}]];
 const results=[];
 for(const [port,name,args] of checks) {
  const result=await call(port,'tools/call',{name,arguments:args});
  results.push({name,result});
  console.log(name,result.isError?'FAIL':'PASS');
  if(result.isError) process.exitCode=1;
  if(name==='search_company_news' && !result.isError) {
    const summary=JSON.parse(result.content[0].text);
    const match=summary.items.map(i=>i.summary.match(/\[articleId: (\d+)\]/)).find(Boolean);
    if(match){
      const article=await call(5211,'tools/call',{name:'read_news_article',arguments:{articleId:Number(match[1])}});
      results.push({name:'read_news_article',result:article});
      console.log('read_news_article',article.isError?'FAIL':'PASS');
      if(article.isError)process.exitCode=1;
    }
  }
 }
 await fs.mkdir('.local/validation',{recursive:true});
 await fs.writeFile('.local/validation/mcp-live.json',JSON.stringify({checkedAt:new Date().toISOString(),results},null,2));
}
