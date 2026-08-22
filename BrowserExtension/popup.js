function fmt(seconds){seconds=Math.max(0,Number(seconds)||0);const h=Math.floor(seconds/3600);const m=Math.floor((seconds%3600)/60);const s=seconds%60;return h>0?`${String(h).padStart(2,'0')}:${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')}`:`${String(m).padStart(2,'0')}:${String(s).padStart(2,'0')}`}
function friendlyCategory(v){const s=String(v||'Neutral').toLowerCase();if(s.includes('entertain'))return 'Giải trí';if(s.includes('focus'))return 'Học / Làm việc';return 'Chưa phân loại'}
async function render(){
  const data=await chrome.storage.local.get('focusLockBridge');
  const d=data.focusLockBridge||{};
  const status=document.getElementById('status');
  status.textContent=d.bridgeOnline?'Đã kết nối':'Chưa kết nối';
  status.className='status '+(d.bridgeOnline?'ok':'bad');
  document.getElementById('host').textContent=d.host||'—';
  document.getElementById('category').textContent=friendlyCategory(d.category);
  document.getElementById('rule').textContent=d.ruleName||'Chưa có quy tắc';
  document.getElementById('balance').textContent=fmt(d.entertainmentBalanceSeconds);
  document.getElementById('profile').textContent='Profile: '+(d.profileName||'—');
  document.getElementById('access').textContent=d.accessMode||'—';
  document.getElementById('message').textContent=d.message||'Mở FocusLock nếu Extension chưa kết nối.';
}
document.getElementById('refresh').addEventListener('click',async()=>{await chrome.runtime.sendMessage({type:'reportNow'});setTimeout(render,250)});
render();
