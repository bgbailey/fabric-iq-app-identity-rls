import json
from pathlib import Path
import build_customer_deck as b
from fabric_architect.model_workflow import run_model, sha256

image=b.STAGE/"implementation-render-v2-final"/"slide-02.png"
air=b.STAGE/"implementation_v2.air.json"
deck=b.STAGE/"v2"/"Fabric_ISV_Identity_and_Analytics_v2.pptx"
prompt=(
    "Review this native PowerPoint request-path diagram for clarity, layout, semantics and actual "
    "implementation accuracy. It is NOT proposed architecture. Four actual components: synthetic "
    "user selector (not login), IQmetadata+GPT5.4planner, .NETQueryBroker, PowerBIsemanticmodel. "
    "Arrow labels may be shortened but identities and direction must match the AIR. Return arrows "
    "intentionally omitted; caption says onlyfilteredrows return. The four separate authentication "
    "contexts are documented on adjacent slide, not needed as more boxes. Check text overlap and "
    "unjustified product/productionclaims. Return JSON only {passed:boolean,findings:[{severity:"
    "\"high|medium|low\",problem:string,correction:string}],summary:string}. No high/medium issues "
    "allowed forpassed. Screenshot alone doesnotproveeditability. AIR:\n"+air.read_text(encoding="utf-8")
)
review,receipt=run_model(prompt,role="visual-review",attachments=[image])
receipt.update(pptx_sha256=sha256(deck),air_sha256=sha256(air),image_sha256=sha256(image))
out=b.STAGE/"implementation-review_v2.json"
with out.open("x",encoding="utf-8") as stream:
    json.dump({"review":review,"receipt":receipt},stream,indent=2)
print(json.dumps(review,indent=2))
raise SystemExit(0 if review.get("passed") else 1)
