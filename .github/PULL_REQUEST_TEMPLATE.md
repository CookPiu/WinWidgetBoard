<!--
English or Chinese are both fine. Keep one purpose per pull request.
-->

## What and why

<!-- What changed, and the problem it solves. Link the issue or ADR if there is one. -->

## Verification

Risk tier (see [CONTRIBUTING.md](https://github.com/CookPiu/WinWidgetBoard/blob/main/CONTRIBUTING.md#how-much-verification-your-change-needs)): **L0 / L1 / L2 / L3**

<!--
Paste the commands you actually ran and their result. If you skipped something the tier asks for,
say so and why — an honest gap is fine, an unverified claim is not.
-->

```
```

## Checklist

- [ ] Build is clean — `TreatWarningsAsErrors` is on, so a new warning fails CI.
- [ ] Related unit tests pass; a bug fix carries a repro or regression test.
- [ ] The change stays inside the existing scope and does not cross a process boundary listed in CONTRIBUTING.md.
- [ ] No new dependency, or a new one is justified and recorded in `docs/development/dependencies.md`.
- [ ] User-visible strings were added to **both** `Strings/en-US/Resources.resw` and `Strings/zh-CN/Resources.resw`.
- [ ] Visible UI changes follow `docs/02-ux-design-spec.md`, and the PR states Before / After / Why.
- [ ] Documentation for any moved behavior, scope or contract is updated in the **one** owning document, in this same change.
- [ ] No secret, token, key, absolute personal path or private data in the diff or the logs pasted above.
