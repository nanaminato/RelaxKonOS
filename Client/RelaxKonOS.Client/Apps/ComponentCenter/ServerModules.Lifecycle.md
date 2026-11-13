# Server module lifecycle on desktop

The Component Center server-modules page exposes installation and version updates using the existing trusted package upload/catalog workflow. Both operations show a frozen plan before application; expired package plans cannot be submitted and previews can be discarded.

## Decision surfaces are dialogs, not page sections

Every irreversible step is confirmed in its own modal window owned by the Component Center:

- Selecting a lifecycle action (enable, disable, remove, wake, sleep, stop, restart, restore-all) or finishing the residency-policy editor prepares the frozen plan and opens a review dialog showing the exact targets, the added/removed enabled set, the restart requirement, the expiry and any blockers. Only that dialog can submit the plan.
- Uploading a `.rkmodule`, or downloading a store release, opens the same review surface for the package plan (module, version, publisher, release id, digest, runtime, declared privileges).
- Privilege approval and standing background permission each open a dialog. Administrator credentials exist only inside those dialogs; no password field remains on the read-only security page.
- Residency policy is read as two facts on the page and edited in a draft dialog that ends in "preview", never in "apply".

A closed dialog is not a decision: the plan (and an uploaded package artifact) stays pending, and the page states it as one row — "pending plan · <action> · <target>" on the module page, "pending plan · <module> · <version>" on the packages page — with *Review plan* and *Discard plan*. Only the explicit discard drops a plan. The view model exposes these surfaces as host-provided callbacks (`PlanReviewRequested`, `PackagePlanReviewRequested`, `PolicyEditorRequested`, `PrivilegeReviewRequested`, `ConsentEditorRequested`), so headless checks keep the protocol path without a window.

Dense reference data (configuration owner, process generation, runtime identifier, host API, release level, capabilities, dependencies and dependents) sits in one collapsed *More technical details* disclosure; the identity card keeps the six facts a decision needs.

The module action list now includes **Preview uninstall**. It accepts one module at a time and uses the server's Remove admission and plan, rather than treating disable, stop or memory release as uninstall. The confirmation dialog explicitly preserves business data, cloud account settings and remote cloud files. Disable the module first; if the server still has it loaded, complete the required server restart before removal. Dependency and runtime blockers are returned by the server. Removed package versions are retained in the server recovery archive; this is not a data purge or a promise to reclaim all package disk space. After application the client refreshes module and package facts.

The client does not independently grant uninstall permission: controller authorization, package ownership and runtime admission remain server decisions. Bundled or non-removable modules display their refusal reason. Changing the logged-in connection invalidates commands through the existing management client.

Verification: regression coverage checks exact single-target preview, exclusion from the enabled set, explicit confirmation/cancel behavior, observer refusal, expired package plans and preview cancellation. Native Windows UI and real server lifecycle acceptance must be recorded separately; this document does not claim on-device verification.
