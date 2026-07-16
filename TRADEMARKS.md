<!-- DRAFT FOR OWNER REVIEW
This is a draft trademark policy for the vouchfx project.
Before publication, the owner must decide on:
1. Contact address for permission requests and misuse reports (currently a placeholder).
2. Whether providers forked from Core versions may retain "for vouchfx" descriptors in their names (open policy question).
3. Any planned trademark registration (current policy applies whether registered or unregistered).
4. The response-time commitments in "Questions, permission requests, and reporting misuse" (currently 14 days for permission requests, 7 days for misuse reports) — these are commitments the owner must be willing to staff.
-->

# vouchfx Trademark Policy

The vouchfx project respects open-source principles and community contribution. This policy ensures the vouchfx name, wordmark, and Vouched badge remain reliable signals of origin and review status, whilst permitting lawful community use without excessive restrictions.

## Marks covered by this policy

- **The "vouchfx" name and wordmark** — used in project identification, documentation, package identifiers, and tooling.
- **The Vouched badge** — a maintainer-awarded mark signifying that a community provider has passed the published review rubric (see [`GOVERNANCE.md`](GOVERNANCE.md#the-vouched-badge-rubric)) for a specific version.

This policy applies to these marks whether or not they are registered with trademark authorities.

## Why this policy exists

The Apache-2.0 licence grants you freedom to use, modify, and distribute the vouchfx code. The trademark policy is separate: it protects the integrity of the vouchfx name and the Vouched badge as signals of provenance and quality. Honouring the policy keeps the community's trust in what "vouchfx" means.

## Permitted uses (no permission needed)

You may use the vouchfx name and marks for truthful, non-misleading purposes without asking:

- **Nominative fair use.** Truthful statements like "built with vouchfx", "compatible with vouchfx", "tested using vouchfx", or "a vouchfx provider".
- **Unmodified redistribution.** Redistributing vouchfx in unmodified form under its original name, with attribution.
- **Community providers.** Authoring and distributing step providers following the documented naming conventions:
  - Providers hosted in the vouchfx-providers hub use the `Vouchfx.Community.<Name>` NuGet package ID and namespace (e.g. `Vouchfx.Community.JsonRpc`).
  - Externally-hosted providers use their own namespace following the `<Org>.Steps.<Name>` convention (e.g. `Acme.Steps.CustomDb`).
  - See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the provider authoring guide.
- **Linking, hyperlinking, and referential use.** Pointing to vouchfx documentation, repositories, or binaries, with clear attribution.
- **Screenshots and examples.** Reproducing vouchfx output or interface elements in documentation, blog posts, tutorials, and educational materials, with attribution.
- **Review, commentary, and academic use.** Discussing vouchfx in peer-reviewed publications, conference talks, technical blogs, and teaching materials.

## Uses requiring permission

You must ask for permission before:

- **Using the vouchfx name or marks in a product name, company name, or domain name.** For example, "VouchFX Solutions Ltd" or a domain like "myvouchfx.com".
- **Distributing a modified fork under the vouchfx name.** If you fork vouchfx and make changes, you must use a distinct name. You *may* state derivation (e.g. "derived from vouchfx") but must not use "vouchfx" or "VouchFX" as the primary name.
- **Creating merchandise, t-shirts, or physical goods** bearing the vouchfx name or Vouched badge.
- **Implying sponsorship, endorsement, or affiliation** with the vouchfx project for your own work, products, or services.

To request permission, contact [TRADEMARK-CONTACT — owner to provide].

## Never permitted

- **The Vouched badge on unpublished or unreviewed provider versions.** The Vouched badge is awarded by a maintainer only after successfully passing the published rubric (see [`GOVERNANCE.md`](GOVERNANCE.md#the-vouched-badge-rubric)). Do not use it on your own work or versions not formally reviewed. The badge records the exact reviewed version and is point-in-time.
- **Reserved NuGet package IDs and namespaces.** The `Vouchfx.*` NuGet ID prefix and the `Vouchfx.Engine.*` and `Vouchfx.Steps.*` namespaces are reserved to the vouchfx project. Publishing packages with these prefixes or declaring types in these namespaces (other than as part of the official vouchfx distribution) is not permitted.
- **Confusingly similar marks or identifiers.** Do not create package IDs, domain names, or product names that are likely to cause confusion with vouchfx, even if they are not identical.
- **Implying official status for unendorsed community work.** Hosting does not imply endorsement. Community providers on the vouchfx-providers hub are listed as contributed work; do not represent them as officially maintained by the vouchfx platform team unless they carry the Vouched badge. See the hub's contributing guide for the distinction.

## Logo and wordmark usage

The vouchfx name is styled in lowercase (`vouchfx`) throughout the project. If a wordmark or logo graphic is created in future, a separate section will detail its use. Until then, refer to the project as "vouchfx" in text.

When the mark appears in code, documentation, or technical writing, match the surrounding context: sentences may capitalise it normally, but product names and identifiers remain lowercase unless your branding guidelines require otherwise (e.g. "VOUCHFX" in all caps for a title).

## Questions, permission requests, and reporting misuse

**To ask for permission:** Contact [TRADEMARK-CONTACT — owner to provide] with your intended use, and we will respond within 14 calendar days.

**To report misuse or infringement:** If you believe someone is using the vouchfx name, wordmark, or Vouched badge in violation of this policy, please report it to [TRADEMARK-CONTACT — owner to provide]. Please include:
- A description of the misuse.
- A link or screenshot of the infringing material.
- Information about the responsible party (if known).

We will review and respond within 7 calendar days.

## Changes to this policy

This policy may be updated as the project grows. The authoritative version is in the main vouchfx repository. Significant changes will be announced via the project's release notes and community channels. Your use of vouchfx continues to be governed by the version of this policy current at the time of use.

## Relationship to the Apache-2.0 licence

This trademark policy is distinct from the vouchfx Apache-2.0 software licence (see [`LICENSE`](LICENSE)). The Apache-2.0 licence grants rights to use, modify, and redistribute the vouchfx code and documentation. This policy addresses the vouchfx name, wordmark, and Vouched badge — marks that signal origin and review status — and is in addition to the licence, not in place of it.

For more on how the vouchfx project makes decisions, see [`GOVERNANCE.md`](GOVERNANCE.md).
