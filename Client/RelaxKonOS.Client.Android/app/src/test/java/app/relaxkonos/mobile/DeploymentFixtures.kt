package app.relaxkonos.mobile

import app.relaxkonos.mobile.core.net.*

fun deploymentFixture() = DeploymentApplication(
    "d3708cc7-3e7e-42ad-b498-11466a48af23", "website", "image", "web", "running", "running", "http", 1, "container", 8080, 9080,
    "127.0.0.1", "Site01", "website.test", null, "personal-site", "1.0.0", "/ready",
    DeploymentLimits(1.5, 16777217, 512), listOf(DeploymentVolume("data", "/app/data:live", true)),
    listOf(DeploymentDefinitionConfig("TEXT", " value=kept ", false, null), DeploymentDefinitionConfig("TOKEN", "saved-secret", true, 7)),
    "2026-10-01T00:00:00.1234567+00:00",
)
