# Code signing policy

**Status: not enrolled or approved for SignPath Foundation signing.** No current release is represented as signed by the Foundation. Version 1.7.1 portable distributions remain unsigned; 1.6.1 signing configuration is retained without a claim of enrollment. The Ed25519 update feed authenticates update packages; it does not grant Windows publisher trust or Apple notarization.

MathGPT is preparing an application for the Foundation's free open-source signing program. This document does not assert approval, sponsorship, or a signing partnership.

## Project and release controls

- The public source must correspond to the distributed desktop program. The MIT desktop application and AGPL adapter retain their own notices and corresponding sources.
- Only this project's own binaries may be signed using a future project policy. Vendor clients and runtime components retain their original publisher signatures and licensing.
- Changes to release code and workflows require review. Maintainers, reviewers and release approvers must use MFA. The actual public repository and named account roles will be published only after the owner has approved that repository and the signing application.
- Each production signing request requires explicit approval. Untrusted pull requests cannot trigger production signing.
- Program files, the adapter, uninstaller and installer must pass signature validation before a Windows release is declared ready. Hashes are computed only from final files.
- Failed, unsigned, test-signed or unnotarized artifacts must not be represented as production-signed releases.
- Approval for signing does not authorize code changes or sharing local user data. Credentials remain in a certificate provider, OS store, Keychain or restricted CI secrets.

## Privacy

The desktop client stores user-supplied API credentials in Windows DPAPI or macOS Keychain. It connects to the API endpoints chosen by the user; website pairing and client installation are initiated by the user. Complete, unmodified Codex and Claude packages use MathGPT distribution paths with pinned hashes and original publisher verification; other official-client downloads retain the vendor source. Download requests do not carry API keys or account sessions. No private server deployment configuration, payment data or signing credential belongs in the public source or distribution.

Any future automatic collection or transfer beyond user-requested operations requires a corresponding privacy update and appropriate user controls. Installation and client configuration must explain their system changes and provide removal or restoration paths.

## If the Foundation accepts the project

The approved policy page will identify the actual repository, maintainers/reviewers and approvers and include the Foundation's required attribution. That attribution is deliberately absent until approval; its absence does not imply that signing is complete.

Reference: https://signpath.org/terms.html
