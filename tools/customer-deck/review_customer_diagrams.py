"""Run the required scoped model review of fresh native PowerPoint diagram exports."""
import json
import os
from pathlib import Path
import sys

SKILL = Path.home() / ".copilot" / "skills" / "fabric-architect"
sys.path.insert(0, str(SKILL))
from fabric_architect.model_workflow import run_model, sha256

ROOT = Path(os.environ["LOCALAPPDATA"]) / "scout" / "build" / "iq-isv-customer"
render = ROOT / sys.argv[1]
images = [render/"slide-03.png", render/"slide-07.png"]
airs = [ROOT/"security_v2.air.json", ROOT/"parity_v1.air.json"]
prompt = (
    "Review these TWO freshly rendered native PowerPoint architecture slides for a customer ISV "
    "security presentation. You are the required gpt-6-astra visual reviewer. Do not use tools. "
    "Images are ordered security then Embedded comparison. Review text collisions/clipping, "
    "labels over connectors, misleading boundaries/directions, icon contrast, and whether the "
    "simplified topology matches its AIR. Native PowerPoint uses grouped nodes and attached "
    "connectors; do not demand draw.io or claim you verified editability. The labels may be "
    "shortened without changing meaning. Security is REQUEST-PATH-only; no response arrows are "
    "intended and its footer explicitly explains filtered results return. Power BI Embedded node "
    "abstracts backend GenerateToken plus report consumption. Both diagrams are proposals, not "
    "completed app integration. Actual RLS query mechanism has42synthetic livechecks but liveIQ/"
    "Embedded parity remains pending. Model RLS depends on mandatory role/key at trusted broker; "
    "the query principal is privileged. Detailed exact API/scopes belong in speaker notes and "
    "adjacent slides; do not force tiny protocol fields onto a marketing diagram. "
    "Return ONLY JSON: {\"passed\":boolean,\"reviewed_slides\":[3,7],"
    "\"findings\":[{\"slide\":number,\"severity\":\"high|medium|low\","
    "\"problem\":string,\"correction\":string}],\"summary\":string}. "
    "passed must be false for any high or medium finding. No praise padding. AIRs:\n" +
    json.dumps([json.loads(p.read_text(encoding="utf-8")) for p in airs],separators=(",",":"))
)
result, receipt = run_model(prompt, role="visual-review", attachments=images)
receipt["images"] = [{"path":str(p),"sha256":sha256(p)} for p in images]
receipt["airs"] = [{"path":str(p),"sha256":sha256(p)} for p in airs]
receipt["pptx_sha256"] = sha256(ROOT/"v1"/"Fabric_ISV_Identity_and_Analytics_v1.pptx")
out = ROOT / ("diagram-review-" + sys.argv[1] + ".json")
with out.open("x",encoding="utf-8") as f:
    json.dump({"review":result,"receipt":receipt},f,indent=2)
print(json.dumps(result,indent=2))
raise SystemExit(0 if result.get("passed") else 1)
