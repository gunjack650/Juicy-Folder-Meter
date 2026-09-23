# SignPath Foundation setup checklist

This repository is prepared for the zero-cost open-source signing route. Acceptance by SignPath Foundation is not automatic.

1. Create a **public GitHub repository** for Juicy Folder Meter and push this source tree.
2. Enable **two-factor authentication** on the GitHub account used to maintain the project.
3. Run the included GitHub Actions workflow and verify that it produces the unsigned MSI as a GitHub-hosted workflow artifact.
4. Publish/document version 1.1.0 publicly (the project must already be released/documented in the form to be signed under SignPath Foundation's current OSS conditions).
5. Keep the MIT license, Privacy page, and **Code signing policy** visible from the repository/release page.
6. Apply for a free SignPath Foundation open-source subscription: https://signpath.org/apply.html
7. If accepted, install the SignPath GitHub App, configure GitHub.com as the trusted build system, create the project/signing policy, and upload or use `signpath/artifact-configuration.xml` as the starting artifact configuration.
8. Replace the generic role text in `CODE_SIGNING_POLICY.md` with the final GitHub profile/team links requested by SignPath.
9. Add the SignPath submission step to the GitHub Actions workflow using the organization ID, project slug, signing-policy slug, artifact-configuration slug and API token supplied/configured in SignPath.
10. Each release signing request requires manual approval under the Foundation's current policy.

## Important

SignPath Foundation states that executable projects need to meet its acceptance criteria and that approval is discretionary. A new or little-known project can be rejected even if the technical requirements are met. Do not promise a Microsoft Store release until the OSS signing application is accepted.
