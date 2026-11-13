# Server module lifecycle on desktop

The Component Center server-modules page exposes installation and version updates using the existing trusted package upload/catalog workflow. Both operations show a frozen plan before application; expired package plans cannot be submitted and previews can be discarded.

The module action list now includes **Preview uninstall**. It accepts one module at a time and uses the server's Remove admission and plan, rather than treating disable, stop or memory release as uninstall. The confirmation card explicitly preserves business data, cloud account settings and remote cloud files. Disable the module first; if the server still has it loaded, complete the required server restart before removal. Dependency and runtime blockers are returned by the server. Removed package versions are retained in the server recovery archive; this is not a data purge or a promise to reclaim all package disk space. After application the client refreshes module and package facts.

The client does not independently grant uninstall permission: controller authorization, package ownership and runtime admission remain server decisions. Bundled or non-removable modules display their refusal reason. Changing the logged-in connection invalidates commands through the existing management client.

Verification: regression coverage checks exact single-target preview, exclusion from the enabled set, explicit confirmation/cancel behavior, observer refusal, expired package plans and preview cancellation. Native Windows UI and real server lifecycle acceptance must be recorded separately; this document does not claim on-device verification.
