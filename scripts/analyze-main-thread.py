import csv,json,math,sys
from pathlib import Path
p=Path(sys.argv[1])
rows=[{k:float(v) for k,v in r.items()} for r in csv.DictReader((p/'frames.csv').open()) if r['complete']=='1']
def stats(rs):
    v=sorted(r['kist_union_ms'] for r in rs)
    def pct(q): return v[max(0,math.ceil(len(v)*q)-1)] if v else 0
    return dict(frames=len(rs),wall_seconds=sum(r['frame_wall_ms'] for r in rs)/1000,kist_total_ms=sum(v),mean_ms=sum(v)/len(v) if v else 0,p50_ms=pct(.5),p95_ms=pct(.95),p99_ms=pct(.99),max_ms=max(v,default=0),over_16_67_ms=sum(x>16.667 for x in v),over_50_ms=sum(x>50 for x in v),over_100_ms=sum(x>100 for x in v),gc0=sum(r['gc0'] for r in rs),gc1=sum(r['gc1'] for r in rs),gc2=sum(r['gc2'] for r in rs))
# UI state is sampled at frame entry, so the opening frame still says closed.
opening=next((i for i,r in enumerate(rows) if r['KistUI.Open_inclusive_ms']>0),None)
transfers=[r for r in rows if r['KistClient.Deposit_inclusive_ms'] or r['KistClient.Take_inclusive_ms']]
report={'scenario':'User-reported single listen-host, no second client; idle, open Kist, withdraw approximately 16 stacks, deposit back. Captured 15 Take calls and 15 Deposit calls.', 'all':stats(rows),'idle_before_opening_call':stats(rows[:opening]) if opening is not None else None,'opening_frame':rows[opening] if opening is not None else None,'after_opening_frame':stats(rows[opening+1:]) if opening is not None else None,'transfer_frames':stats(transfers),'initial_stacks':rows[0]['stacks'],'min_stacks':min(r['stacks'] for r in rows),'final_stacks':rows[-1]['stacks'],'limitations':'Instrumented synchronous elapsed time, nested calls deduplicated. Full frame wall includes GPU/VSync/OS. Deferred all-game canvas work is separate. Method totals overlap. Current legacy gameplay, not journal cutover.'}
(p/'analysis.json').write_text(json.dumps(report,indent=2))
print(json.dumps({k:v for k,v in report.items() if k!='opening_frame'},indent=2))
