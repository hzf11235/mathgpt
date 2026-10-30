# Code signing policy

**Status: not enrolled or approved for SignPath Foundation signing.** No current release is represented as signed by the Foundation. Published version 1.8.2 remains unsigned. Signing configuration follows the checked-out release metadata, without a claim of enrollment. The Ed25519 update feed authenticates update packages; it does not grant Windows publisher trust or Apple notarization.

MathGPT is preparing an application for the Foundation's free open-source signing program. This document does not assert approval, sponsorship, or a signing partnership.

Repository: https://github.com/hzf11235/mathgpt . The repository owner [hzf11235](https://github.com/hzf11235) maintains this project and reviews contributed changes. The same owner is the designated release approver; no independent reviewer is claimed. MFA verification and Foundation enrollment remain pending, so no production signing approval has been issued.

## Project and release controls

- The public source must correspond to the distributed desktop program. The MIT desktop application and AGPL adapter retain their own notices and corresponding sources.
- Only this project's own binaries may be signed using a future project policy. Vendor clients and runtime components retain their original publisher signatures and licensing.
- Changes to release code and workflows require review. Maintainers, reviewers and release approvers must use MFA before production signing is enabled. The public source is published with the owner's authorization; this does not establish that MFA or Foundation enrollment has been completed.
- Each production signing request requires explicit approval. Untrusted pull requests cannot trigger production signing.
- Program files, the adapter, uninstaller and installer must pass signature validation before a Windows release is declared ready. Hashes are computed only from final files.
- Failed, unsigned, test-signed or unnotarized artifacts must not be represented as production-signed releases.
- Approval for signing does not authorize code changes or sharing local user data. Credentials remain in a certificate provider, OS store, Keychain or restricted CI secrets.

## Privacy

The desktop client stores user-supplied API credentials in Windows DPAPI or macOS Keychain. It connects to the API endpoints chosen by the user; website pairing and client installation are initiated by the user. The unpublished 1.8.3 candidate discloses automatic update and account-refresh traffic in the installer and provides separate controls. Without confirmed choices these automatic operations remain off; manual requests remain available. Complete, unmodified Codex and Claude packages use MathGPT distribution paths with pinned hashes and original publisher verification; other official-client downloads retain the vendor source. Download requests do not carry API keys or account sessions. No private server deployment configuration, payment data or signing credential belongs in the public source or distribution. See [the candidate privacy description](docs/PRIVACY.md).

The new privacy controls still require release acceptance before the candidate replaces 1.8.2. Foundation enrollment, public repository authorization, MFA and named signing roles remain outstanding. Installation and client configuration explain their system changes and provide removal or restoration paths.

## If the Foundation accepts the project

The approved policy page will identify the actual repository, maintainers/reviewers and approvers and include the Foundation's required attribution. That attribution is deliberately absent until approval; its absence does not imply that signing is complete.

Reference: https://signpath.org/terms.html
