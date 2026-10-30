const fs=require('node:fs/promises');
const sharp=require('sharp');
(async()=>{
  await fs.mkdir('channel/appPackage',{recursive:true});
  await sharp('assets/brand/lges-logo-en.svg',{density:300}).resize(160,130,{fit:'inside'}).png().toBuffer().then(async logo=>
    sharp({create:{width:192,height:192,channels:4,background:'#FFFFFF'}}).composite([{input:logo,gravity:'centre'}]).png().toFile('channel/appPackage/color.png'));
  const outline=await sharp('assets/brand/lges-logo-en-white.svg',{density:300}).resize(30,30,{fit:'inside'}).png().toBuffer().then(async logo=>
    sharp({create:{width:32,height:32,channels:4,background:'#00000000'}}).composite([{input:logo,gravity:'centre'}]).ensureAlpha().raw().toBuffer({resolveWithObject:true}));
  // The official white wordmark still has a red symbol. Teams requires every
  // visible outline pixel to be white; retain the original geometry and alpha.
  for(let i=0;i<outline.data.length;i+=4){
    outline.data[i]=255;outline.data[i+1]=255;outline.data[i+2]=255;
  }
  await sharp(outline.data,{raw:outline.info}).png().toFile('channel/appPackage/outline.png');
  await sharp('assets/brand/lges-logo-ko.svg',{density:300}).resize(1059,168).png().toFile('assets/brand/lges-logo-ko.png');
})();
