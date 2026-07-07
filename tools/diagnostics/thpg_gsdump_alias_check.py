import csv, struct, sys, os
from collections import defaultdict

def s16(d,o): return struct.unpack_from('<h',d,o)[0]

def parse_batches(data):
    out=[]; i=0; end=len(data)-8
    while i<end:
        if data[i]==3 and data[i+1]==1 and data[i+3]==0x01:
            j=i+4
            if j+4<=len(data) and (data[j+3]&0x6F)==0x69:
                n=data[j+2] or 256
                ps=((6*n+3)>>2)<<2; k=j+4+ps
                if k+4<=len(data) and (data[k+3]&0x6F)==0x6A and (data[k+2] or 256)==n:
                    ns=((3*n+3)>>2)<<2; m=k+4+ns
                    if m+4<=len(data) and (data[m+3]&0x6F)==0x6D and (data[m+2] or 256)==n:
                        verts=[]
                        for v in range(n):
                            fine=tuple(s16(data,j+4+v*6+c*2)/4096.0 for c in range(3))
                            uv=(s16(data,m+4+v*8)/4096.0, s16(data,m+4+v*8+2)/4096.0)
                            verts.append((fine,uv))
                        out.append(verts); i=m+4+8*n; continue
        i+=4
    return out

def quv(u,v): return (round(u*512),round(v*512))

draws=defaultdict(list)
with open(sys.argv[1], newline='') as f:
    for row in csv.DictReader(f):
        if row['prim']!='4': continue
        q=float(row['q'])
        if q==0: continue
        draws[(int(row['vsync']),int(row['giftag']))].append(
            (float(row['s'])/q, float(row['t'])/q, float(row['x']), float(row['y'])))
draw_fp={k: frozenset(quv(u,v) for u,v,_,_ in vs) for k,vs in draws.items()}

wrapped_batches=0; alias_pairs=0
alias_screen=[]; ref_screen=[]
for path in sys.argv[2:]:
    data=open(path,'rb').read()
    name=os.path.basename(path)
    for bi,verts in enumerate(parse_batches(data)):
        for axis in range(3):
            vals=sorted(f[axis] for f,_ in verts)
            rng=vals[-1]-vals[0]
            if rng<14: continue
            gaps=[(vals[i+1]-vals[i]) for i in range(len(vals)-1)]
            if max(gaps)<6: continue
            wrapped_batches+=1
            bfp=set(quv(u,v) for _,(u,v) in verts)
            best=None
            for key,fp in draw_fp.items():
                sc=len(bfp&fp)/len(bfp)
                if best is None or sc>best[0]: best=(sc,key)
            if best[0]<0.8: continue
            gs=draws[best[1]]
            gs_by_uv=defaultdict(list)
            for u,v,x,y in gs: gs_by_uv[quv(u,v)].append((x,y))
            uvs={k:v[0] for k,v in gs_by_uv.items() if len(set(v))==1}
            n=len(verts)
            for i in range(n):
                for j in range(i+1,n):
                    (f1,uv1),(f2,uv2)=verts[i],verts[j]
                    d=abs(f1[axis]-f2[axis])
                    other=[abs(f1[c]-f2[c]) for c in range(3) if c!=axis]
                    q1,q2=quv(*uv1),quv(*uv2)
                    if q1 not in uvs or q2 not in uvs or q1==q2: continue
                    s1,s2=uvs[q1],uvs[q2]
                    sd=((s1[0]-s2[0])**2+(s1[1]-s2[1])**2)**0.5
                    if d>14.5 and max(other)<2.0:
                        alias_pairs+=1
                        alias_screen.append((name,bi,axis,i,j,16-d,sd))
                    elif d<2.0 and max(other)<2.0:
                        ref_screen.append(sd)
            break
print(f"wrap-signature batches: {wrapped_batches}, alias pairs found: {alias_pairs}")
if ref_screen:
    ref_screen.sort()
    print(f"reference pairs (true dist <2.8u): {len(ref_screen)}, median {ref_screen[len(ref_screen)//2]:.1f}px, p90 {ref_screen[int(len(ref_screen)*0.9)]:.1f}px")
for name,bi,axis,i,j,truedist,sd in alias_screen[:25]:
    print(f"  {name} b{bi} ax{axis} v{i}-v{j}: true~{truedist:.2f}u apart -> screen {sd:.1f}px")
