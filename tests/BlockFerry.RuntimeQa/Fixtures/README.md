# BlockFerry runtime QA fixture root

This directory is intentionally inert. The runtime launch harness snapshots every
file here before starting the app and verifies the exact relative path, length,
and SHA-256 map again after a graceful close.

No production Minecraft instance belongs in this directory. End-to-end adapter
and transaction fixtures are generated in temporary directories by their owning
test suites.
