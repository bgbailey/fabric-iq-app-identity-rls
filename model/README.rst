Synthetic app-identity RLS model
================================

Status: local artifacts only; not live validated or deployed.

The fixture's schemaVersion is 1, logical modelAlias is synthetic-rls-v1,
and entitlementVersion is synthetic-v1, matching the trusted broker contract.

Synthetic.SemanticModel is the semantic-model folder portion of a Power BI
project, with definition.pbism 4.2 and TMDL compatibility level 1702.
It is not a report or a complete report-launching .pbip project.

All data is synthetic and is held in four typed inline #table M partitions.
There are no SQL, OneLake, gateway, connection or external-source dependencies.
Import processing is still required after any future deployment; metadata
creation alone does not populate the model.

Activity has six rows totaling 1950. Scope has four customer/product pairs:
A/Home, A/Auto, B/Home, B/Auto. Date is a neutral 31-day January 2026 calendar.
The explicit measures are Total Amount and Activity Count on Activity.
Their raw values are BLANK for an empty result, not zero.

Intended security contract
--------------------------

ExternalAppScope is a read-only role with direct row filters on Scope and
User Access. It requires nonblank CUSTOMDATA and an exact immutable subject
match using the case-sensitive DAX EXACT function. Scope membership uses
the single Scope Key, never independent customer and product lists.

User Access is deliberately disconnected. Its own subject-only filter
prevents entitlement enumeration; its policy does not reference Scope.
Scope can consult User Access without creating a reciprocal RLS dependency.
Active one-to-many Scope-to-Activity filtering is single-direction for both
normal and security filtering. Date-to-Activity is also single-direction.
The relationship path is intended to protect unrestricted fact queries,
not just queries whose author remembered to include a scope filter.

MetadataOnly denies all Scope and User Access rows, with intended Activity
denial through the same relationship. It leaves neutral Date visible.
This role is not object-level security and does not hide schema metadata.
Roles are additive: do not combine it with an allowing role expecting a deny
to override an allow. No real role memberships are baked into the model.

Hidden columns/tables are usability settings, NEVER a security boundary.
CUSTOMDATA is not authentication: the trusted broker must derive it from
an authenticated application subject, not accept a caller-selected subject.
Roles absent from a session, elevated service permissions, or different
identity transport can invalidate the intended RLS context. A role definition
alone is no proof against those paths.

Fixture expectations
---------------------

================  ===========  ============  ==============
Subject suffix    Scope Keys   Total Amount  Activity Count
================  ===========  ============  ==============
A1                1            250           2
A2                2            100           2
A3                1,2          350           4
B1                3            700           1
pairs             1,4          1150          3
none              none         BLANK         BLANK
================  ===========  ============  ==============

Full keys are app-user-A1, app-user-A2, app-user-A3, app-user-B1,
app-user-pairs and app-user-none. The fixture also records missing, empty,
unknown, case-variant and trailing-space negatives, exact fact row IDs,
entitlement counts, and the MetadataOnly expectation. The pairs case must
NOT authorize the A/Auto or B/Home cross-products.

Validation and approval
------------------------

See tools\model-definition\README.rst for exact offline commands. TOM syntax
validation and full metadata round-trip are genuine. Fixture arithmetic,
exact DAX text checks and dependency-free M text checks are local checks,
not execution of either language and not verification of live RLS.

Later, only after explicit approval, a live gate must verify processing,
role activation and identity propagation; unrestricted Activity, Scope and
User Access queries; attempted ALL/REMOVEFILTERS bypass; exact pair grants;
empty/unknown identities; and elevated-role behavior. The neutral calendar
must remain visible, while empty restricted measures remain BLANK.

Public references and upstream paths are recorded in references.json.
This artifact contains no actual user, customer, tenant or subscription data.
