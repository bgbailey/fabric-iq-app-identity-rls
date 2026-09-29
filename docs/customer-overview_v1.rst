Customer overview and simplified security diagrams
==================================================

Version 1, authored 28 September 2026.

``customer-overview/Fabric_ISV_Identity_and_Analytics_v1.pptx`` is a 14-slide
customer narrative: preserve application login and entitlements, use IQ for
semantic context, keep query authorization in the trusted broker, and enforce
access in the semantic model.

``customer-overview/ISV_Security_Diagrams_v1.pptx`` contains three reusable,
native-editable slides: the security request path, the four identity contexts,
and the intended Embedded/conversational comparison. Grouped nodes and connected
PowerPoint arrows remain editable. PNG exports and the two architecture AIR
models are in the same directory.

The comparison distinguishes Embedded effective identity from per-query
``roles``/``customData``. Existing ``USERNAME()``-based roles may need explicit
adaptation; actual Embedded/chat row parity is not claimed.

Evidence boundary
-----------------

The slides cite current public Microsoft documentation and the recorded
42-check synthetic Import-model RLS proof. They explicitly distinguish that
observed mechanism from the still-unproven live IQ-to-DAX-to-answer integration.
No cloud deployment, customer login implementation, Embedded report parity,
production support or public repository release is implied.

All slides are authored; no restricted customer or GearUp slides are reproduced.
Live GearUp discovery was unavailable due to authentication, and the local index
is not represented as a complete current source. Full citations, assumptions and
presenter guidance are in slide notes. Apply the appropriate sensitivity label
before sharing the new output.

Local authoring
---------------

``tools/customer-deck/`` contains the builder, native PowerPoint render and
editability helpers, scoped architecture reviewer and guarded versioned
publisher. It uses the locally installed Microsoft deck/architecture skill
assets, not a self-contained third-party build distribution.

The publisher preserves immutable staged copies and refuses existing output
names. Existing application, cloud configuration and approval state are unchanged.
