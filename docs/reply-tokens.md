# Signed reply tokens

Every host needs a `ReplyTokenKeyRing` in `IxIFlowOptions.ReplyTokenKeys`, including hosts that only run workflows without replies. Supply one current signing key and zero or more previous verification keys. Each key must contain at least 32 cryptographically random bytes. Host construction refuses a missing ring; ring construction refuses any short key. Load keys from your secret manager or protected configuration. Every host using the same store must share the ring. The fluent builder's `ExecuteAsync` also needs a supplied service provider configured with these keys; its unconfigured default provider cannot start.

<!-- snippet: samples/IxIFlow.Samples/ReplyTokenExamples.cs#reply-token-keys -->
```csharp
var current = Convert.FromBase64String(configuration["Workflow:ReplyKeys:Current"]!);
var previous = configuration.GetSection("Workflow:ReplyKeys:Previous").GetChildren()
    .Select(key => Convert.FromBase64String(key.Value!)).ToArray();
services.AddIxIFlow(options => options.ReplyTokenKeys = new ReplyTokenKeyRing(current, previous));
```

Generate the bytes with a cryptographic random generator and persist them securely. Base64 here is only the configuration representation. The executable examples use an ephemeral ring for demonstration; durable deployments must retain their keys across restarts and share them across processes. Never put production keys in source control, diagnostics, request logs or exception messages. The ring copies its inputs and exposes no key material.

The engine signs the version, instance ID, wait ID and optional subject digest with HMAC-SHA256. Tokens remain opaque base64url strings. Treat a token as permission to answer its one wait: signatures prevent forgery but a stolen valid token can still be used. Request activities receive signed tokens through `context.Reply.Token`. Discovery and reply-authorized HTTP listings also sign with the current key, and discovery remains limited to plain event waits.

`ReplyAsync` and the acceptance-only `IWorkflowIngressClient.AcceptReplyAsync` authenticate a token before reading the store or appending to the inbox. Signature comparisons take constant time and check every verification key. Invalid signatures, malformed tokens, unsupported versions and unsigned legacy tokens all fail as an unknown reply token (`InvalidRequest`) without revealing which check failed. There is no unsigned fallback or verification switch. A valid signature still needs an issued, open wait and the correct subject, event type and matching payload. Closed waits retain their specific rejection reasons.

For an incoming CloudEvents event request, send the token in `X-IxIFlow-Reply-Token`. The token's instance must match the route instance. The endpoint verifies it before ownership or status reads, then uses the engine's acceptance-only reply path. Authentication, instance authorization and any configured Standard Webhooks body signature still apply. A token targets its wait regardless of the route's event key. An event without this header remains an ordinary keyed notification. Raw target wait IDs are refused by the ingress client.

Operations listings return `waitRef` for diagnostic correlation and signed tokens only under the reply policy. Their `nextAfter` value is encrypted and authenticated with a separate key derived from the ring, so it hides the wait ID and cannot be used as a reply token; pass it unchanged as `after`. Trace and snapshot access requires only the inspect policy, including authorization against the instance summary. It grants no token discovery permission. Engine traces, snapshots, pending-activity lists and logs contain no token, key or raw wait ID.

## Rotate keys

1. Generate a new key. Deploy a ring to every host with the old key still current and the new key among the verification keys. This prepares all hosts to verify both keys.
2. Deploy a ring with the new key current and the old key among previous keys. Hosts can now issue with either key during the rollout and accept each other's tokens.
3. Keep the old key until every token that must remain usable has been replaced or its wait has closed. Long-lived request links may need the old verification key for their full lifetime.
4. Remove the old key from every host. Tokens signed by that key immediately become unknown. There is no automatic expiry or re-signing of already delivered request links.

Rotate all hosts together for emergency revocation. The ring is immutable; apply configuration changes by rebuilding/restarting the host. Discovery can issue a fresh current-key token for an eligible plain wait. Request recipients need a new link from the application when an old signing key is retired.
