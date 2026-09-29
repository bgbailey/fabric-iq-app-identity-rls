"""Publish the implementation-accurate v2 without changing v1."""
import json
from pathlib import Path
from pptx import Presentation
import build_customer_deck as b
from publish_customer_deck import copy_new, graph, signature, sha, DEST, REPO

stage=b.STAGE/"v2"
overview=stage/"Fabric_ISV_Identity_and_Analytics_v2.pptx"
diagrams=stage/"ISV_Security_Diagrams_v2.pptx"
review=json.loads((b.STAGE/"implementation-review_v2.json").read_text(encoding="utf-8"))
edit=json.loads((b.STAGE/"implementation-editability_v2.json").read_text(encoding="utf-8-sig"))
if not review["review"]["passed"] or review["receipt"]["pptx_sha256"] != sha(overview):
    raise ValueError("Final review does not match the implementation deck")
if not edit["passed"] or edit["sourceSha256"].lower() != sha(overview):
    raise ValueError("Editor observations do not match the implementation deck")
p,d=Presentation(overview),Presentation(diagrams)
air=b.AIR.from_file(stage/"implementation_v2.air.json")
graph(p.slides[1],air)
graph(d.slides[0],air)
for source,target in ((1,0),(2,1),(3,2)):
    if signature(p.slides[source]) != signature(d.slides[target]):
        raise ValueError("Standalone implementation diagrams differ from reviewed deck")
immutable=b.STAGE/"published"/"v2"
immutable.mkdir(parents=True,exist_ok=False)
items=[
    (overview,overview.name),(diagrams,diagrams.name),
    (b.STAGE/"implementation-render-v2-final"/"slide-02.png","Security_Request_Path_v2.png"),
    (b.STAGE/"implementation-render-v2-final"/"slide-03.png","Identity_Boundaries_v2.png"),
    (b.STAGE/"implementation-render-v2-final"/"slide-04.png","Actual_Execution_Flow_v2.png"),
]
receipts=[]
for source,name in items:
    clean=immutable/name
    copy_new(source,clean)
    copy_new(clean,DEST/name)
    copy_new(clean,REPO/name)
    receipts.append({"name":name,"path":str(DEST/name),"sha256":sha(clean),"clean_staged_copy":str(clean)})
copy_new(stage/"implementation_v2.air.json",REPO/"implementation_v2.air.json")
manifest={
    "version":2,"outputs":receipts,
    "overviewSlides":12,"standaloneDiagrams":3,
    "classification":{"own/public-source-derived":{"overview":"1-12","diagrams":"1-3"},
                      "Private Preview":[],"NDA Only":[],"Confidential/PROTECTED":[]},
    "publicSourceReferences":b.SOURCES,
    "evidence":"docs/status.rst; selected live synthetic cases, not full matrix or Embedded parity",
    "nativeGraph":"Matched AIR node/edge identities and attachment IDs",
    "visualReview":review["review"],
    "editability":"Actual PowerPoint movement and label edits preserved groups and connector attachment",
    "publish":"Exact bytes plus slide/recursive shape census matched after initial publish; labelled readback follows",
    "sharing":"New output; confirm sensitivity label and publication permission before redistribution",
}
mp=immutable/"build_manifest_v2.json"
mp.write_text(json.dumps(manifest,indent=2)+"\n",encoding="utf-8")
copy_new(mp,DEST/mp.name)
print(json.dumps(receipts,indent=2))
