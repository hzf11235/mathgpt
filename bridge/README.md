# MathGPT bundled protocol adapter

Independent loopback service; AGPL-3.0-or-later. Protocol conversions reuse
QuantumNous/new-api relaykit, vendored from feng-relay commit 3e5afd7 (relaykit
last changed in commit 6757a97). Upstream: https://github.com/QuantumNous/new-api.
Every shipped binary includes this folder's complete corresponding source and
vendored dependencies as MathGPT.Bridge-source.zip.

MathGPT 1.5.0 extends the vendored Gemini capability table for the officially documented gemini-4-argon low/medium/high levels. Protocol defaults can be supplied per model; explicit client effort parameters take precedence. This adapter remains limited to text and ordinary function tools.

Build with Go 1.27.3 or compatible newer Go:

```
go test -mod=vendor ./...
go build -mod=vendor -trimpath -o MathGPT.Bridge.exe .
```

The development go.mod replacement documents the source provenance; a standalone
source archive builds with -mod=vendor without the website checkout.

Configuration is one JSON document over stdin, never command arguments. MathGPT
stores it using current-user Windows DPAPI. The service binds 127.0.0.1, requires
an independent random local credential, denies browser origins and unexpected
hosts, forwards only allowed exact model IDs to one configured HTTPS gateway,
does no retries, and exits after 15 minutes without requests. It never logs key
material or conversation bodies. Protocol translations cover text and ordinary
function tools; unsupported hosted tools fail explicitly. Native client runtime
compatibility still requires integration tests in the particular client version.
