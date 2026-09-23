# Code signing policy

Free code signing provided by SignPath.io, certificate by SignPath Foundation.

## Project roles

For this small project, the project owner acts as maintainer/committer, reviewer, and release approver. External contributions are reviewed before merge. Release-signing requests are manually approved.

- Committers and reviewers: project owner/maintainer
- Approvers: project owner/maintainer

These role references can be replaced with GitHub team/profile links once the public repository URL is final.

## Build and release policy

Release binaries are built from the public source repository using a GitHub-hosted GitHub Actions runner. The unsigned MSI is uploaded as a workflow artifact before a SignPath signing request is submitted. SignPath origin verification is intended to bind signed releases to the public repository and release workflow.

## Privacy

See [PRIVACY.md](PRIVACY.md). The program does not transfer information to networked systems unless specifically requested by the user or installer/operator.
