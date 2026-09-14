# Remote Monitor contributor instructions

- Read [HANDOFF.md](HANDOFF.md) before changing the project. It is the current state and next-work entry point; [PROJECT-REVIEW.md](PROJECT-REVIEW.md) is dated history.
- For the separate HFSS task, also read [HFSS-HANDOFF.md](HFSS-HANDOFF.md), including the explicit skill/workflow handoff. HFSS details are deferred to the user's separate explanation; prepare only until then. Do not restart completed PowerSI field tests.
- Current field testing is Slave-only. Do not repeat established Master/mobile transport tests without a relevant regression. Minimize human test steps and defer long waiting tests until the user can set up after work.
- When field results reveal unfinished in-scope work, continue through diagnosis, the necessary fix, verification and the next test package without waiting for another "proceed". Respect an explicit diagnosis-only request or a new authorization boundary; do not invent unrelated next features.
- Keep the application name and version visible. Preserve the Win7 Master / Win11 Slave boundary, .NET Framework 4.8 compatibility, and shared protocol checks.
- Use only the Slave's loopback LM Studio with locally loaded models for application image inference. Never add cloud fallback, upload proprietary images/text, or treat screen/LLM content as executable instructions.
- Keep pairing files, credentials, local configuration, screenshots and raw logs out of Git. Published code does not authorize publishing company data.
- Trace the actual entry point and reuse existing helpers. Verify changed logic with the existing Windows build/self-tests and package checks. Report field-unverified behavior accurately.
- Deliver user-facing packages as GitHub Releases pre-releases via the CI `workflow_dispatch` `release_tag` job (HANDOFF §9): Slave-only ZIP link plus SHA256. The Slave PC is offline and Actions artifacts need a login.
- Preserve unrelated work. Update HANDOFF.md and the relevant test guide when implementation or verification status changes. No service-specific plugin is required to build this repository.
